using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
namespace Nutragea.Api.Security;
public sealed class JwtSettings
{
 public string Issuer {get;set;}="";
 public string Audience {get;set;}="";
 public string SigningKeyBase64 {get;set;}="";
 public string EncryptionKeyBase64 {get;set;}="";
 public int LifetimeMinutes {get;set;}=15;
 public byte[] SigningKey => Convert.FromBase64String(SigningKeyBase64);
 public byte[] EncryptionKey => Convert.FromBase64String(EncryptionKeyBase64);
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Issuer))
            throw new InvalidOperationException("Jwt:Issuer is missing.");

        if (string.IsNullOrWhiteSpace(Audience))
            throw new InvalidOperationException("Jwt:Audience is missing.");

        byte[] signing;
        byte[] encryption;

        try
        {
            signing = Convert.FromBase64String(SigningKeyBase64 ?? "");
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                "Jwt:SigningKeyBase64 is not valid Base64.");
        }

        try
        {
            encryption = Convert.FromBase64String(EncryptionKeyBase64 ?? "");
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                "Jwt:EncryptionKeyBase64 is not valid Base64.");
        }

        if (signing.Length < 64)
            throw new InvalidOperationException(
                "Jwt:SigningKeyBase64 must decode to at least 64 bytes.");

        if (encryption.Length != 32)
            throw new InvalidOperationException(
                "Jwt:EncryptionKeyBase64 must decode to exactly 32 bytes.");

        if (LifetimeMinutes is < 1 or > 30)
            throw new InvalidOperationException(
                "Jwt:LifetimeMinutes must be between 1 and 30.");

        if (signing.SequenceEqual(encryption))
            throw new InvalidOperationException(
                "JWT signing and encryption keys must be different.");
    }
}
public sealed class TokenService(JwtSettings settings)
{
 public TokenValidationParameters ValidationParameters => new()
 {
  ValidateIssuer=true,ValidIssuer=settings.Issuer,ValidateAudience=true,ValidAudience=settings.Audience,
  ValidateLifetime=true,RequireExpirationTime=true,RequireSignedTokens=true,ValidateIssuerSigningKey=true,
  IssuerSigningKey=new SymmetricSecurityKey(settings.SigningKey),TokenDecryptionKey=new SymmetricSecurityKey(settings.EncryptionKey),
  ValidAlgorithms=[SecurityAlgorithms.HmacSha512,SecurityAlgorithms.Aes256KW,SecurityAlgorithms.Aes256CbcHmacSha512], ClockSkew=TimeSpan.FromSeconds(15),NameClaimType="name",RoleClaimType="role"
 };
 public (string Token,DateTime ExpiresAt) Issue(long userId,string username,string role,string sessionId)
 {
  var now=DateTime.UtcNow;var expiry=now.AddMinutes(settings.LifetimeMinutes);
  var descriptor=new SecurityTokenDescriptor
  {
   Issuer=settings.Issuer,Audience=settings.Audience,IssuedAt=now,NotBefore=now,Expires=expiry,
   Subject=new ClaimsIdentity(new[]{new Claim("sub",userId.ToString()),new Claim("name",username),new Claim("role",role),new Claim("sid",sessionId),new Claim("jti",Guid.NewGuid().ToString("N"))}),
   SigningCredentials=new SigningCredentials(new SymmetricSecurityKey(settings.SigningKey),SecurityAlgorithms.HmacSha512),
   EncryptingCredentials=new EncryptingCredentials(new SymmetricSecurityKey(settings.EncryptionKey),SecurityAlgorithms.Aes256KW,SecurityAlgorithms.Aes256CbcHmacSha512)
  };
  return(new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor),expiry);
 }
 public static string RandomToken()=>Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
