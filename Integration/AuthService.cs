using Microsoft.AspNetCore.Identity;
using Nutragea.Api.Contracts;
using Nutragea.Api.Data;
using Nutragea.Api.Services;
namespace Nutragea.Api.Security;
public sealed class AuthService(Db db,TokenService tokens)
{
 private static readonly PasswordHasher<string> Hasher=new(new Microsoft.Extensions.Options.OptionsWrapper<PasswordHasherOptions>(new(){IterationCount=210000}));
 private static readonly string DummyHash=Hasher.HashPassword("dummy","a-long-random-dummy-password");
 public static string Hash(string password)=>Hasher.HashPassword("user",password);
 public static bool Verify(string hash,string password)
 { try { return Hasher.VerifyHashedPassword("user",hash,password)!=PasswordVerificationResult.Failed; } catch(FormatException) { return false; } }
 public async Task<object> Login(LoginRequest request,CancellationToken ct)
 {
  // Commit failed-login counters before returning an error (do not throw inside this transaction).
  var result=await db.Write<object?>(async sql=>
  {
   var rows=await sql.Rows("SELECT * FROM users WHERE username=@username FOR UPDATE",new{username=request.Username.Trim()});
   if(rows.Count==0) { Verify(DummyHash,request.Password);return null; }
   var u=rows[0];var id=u.L("id");
   if(!u.B("is_active") || (u["locked_until"] is DateTime until && until>DateTime.UtcNow)) { Verify(DummyHash,request.Password);return null; }
   if(!Verify(u.S("password_hash"),request.Password))
   {
    await sql.Exec("UPDATE users SET failed_attempts=IF(locked_until IS NOT NULL AND locked_until<=UTC_TIMESTAMP(6),1,failed_attempts+1), locked_until=IF(failed_attempts>=5,DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 15 MINUTE),NULL) WHERE id=@id",new{id});
    await sql.Audit(id,"LoginFailed","User",id);return null;
   }
   await sql.Exec("UPDATE users SET failed_attempts=0,locked_until=NULL WHERE id=@id",new{id});
   var sid=TokenService.RandomToken();var issued=tokens.Issue(id,u.S("username"),u.S("role"),sid);
   await sql.Exec("INSERT INTO sessions(id,user_id,expires_at) VALUES(@sid,@id,@expiry)",new{sid,id,expiry=issued.ExpiresAt});
   await sql.Audit(id,"Login","User",id);
   return new{accessToken=issued.Token,tokenType="Bearer",expiresAt=issued.ExpiresAt,user=new{id,username=u.S("username"),role=u.S("role")}};
  },ct);
  return result??throw new ApiException(401,"Invalid credentials or account unavailable.");
 }
 public async Task<bool> ValidateSession(string sid,string userId,string role,CancellationToken ct)
 {
  var rows=await db.Query("SELECT s.id FROM sessions s JOIN users u ON u.id=s.user_id WHERE s.id=@sid AND u.id=@userId AND u.role=@role AND u.is_active=1 AND s.revoked=0 AND s.expires_at>UTC_TIMESTAMP(6)",new{sid,userId,role},ct);
  return rows.Count==1;
 }
 public async Task Logout(long actor,string sid,CancellationToken ct)=>await db.Write(async sql=>{await sql.Exec("UPDATE sessions SET revoked=1 WHERE id=@sid AND user_id=@actor",new{sid,actor});await sql.Audit(actor,"Logout","User",actor);return true;},ct);
 public async Task ChangePassword(long actor,PasswordRequest req,CancellationToken ct)
 {
  await db.Write(async sql=>{
   var u=await sql.One("SELECT * FROM users WHERE id=@actor",new{actor});
   Rules.Require(Verify(u.S("password_hash"),req.CurrentPassword),"Current password is incorrect.",400);
   Rules.Require(req.NewPassword!=req.CurrentPassword,"New password must differ from current password.");
   await sql.Exec("UPDATE users SET password_hash=@hash WHERE id=@actor",new{hash=Hash(req.NewPassword),actor});
   await sql.Exec("UPDATE sessions SET revoked=1 WHERE user_id=@actor",new{actor});
   await sql.Audit(actor,"PasswordChanged","User",actor);return true;
  },ct);
 }
 public async Task<object> CreateUser(long actor,UserRequest req,CancellationToken ct)=>await db.Write<object>(async sql=>{
  var id=await sql.Insert("INSERT INTO users(username,password_hash,role) VALUES(@username,@hash,@role)",new{username=req.Username.Trim(),hash=Hash(req.Password),role=req.Role});
  await sql.Audit(actor,"Created","User",id,new{req.Username,req.Role});return new{id};
 },ct);
 public async Task ChangeAccess(long actor,long id,UserAccessRequest req,CancellationToken ct)=>await db.Write(async sql=>{
  var u=await sql.One("SELECT id FROM users WHERE id=@id",new{id});
  Rules.Require(id!=actor,"You cannot change your own role or active status.");
  await sql.Exec("UPDATE users SET role=@role,is_active=@active WHERE id=@id",new{role=req.Role,active=req.IsActive,id});
  await sql.Exec("UPDATE sessions SET revoked=1 WHERE user_id=@id",new{id});
  await sql.Audit(actor,"AccessChanged","User",id,new{req.Role,req.IsActive});return true;
 },ct);
}
