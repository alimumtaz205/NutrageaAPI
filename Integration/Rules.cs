namespace Nutragea.Api.Services;
public sealed class ApiException(int status, string message) : Exception(message) { public int Status { get; } = status; }
public static class Rules
{
    public static void Require(bool condition,string message,int status=400) { if(!condition) throw new ApiException(status,message); }
    public static void Weight(decimal value) => Require(value>=0 && decimal.Round(value,3)==value,"Weights must be nonnegative with at most 3 decimal places.");
    public static void Receipt(decimal received,decimal accepted,decimal rejected)
    { Weight(received); Weight(accepted); Weight(rejected); Require(received>0 && accepted>0 && received==accepted+rejected,"Received kg must equal accepted plus rejected kg, with positive accepted weight."); }
    public static void Balance(decimal input,decimal output,decimal reduction,decimal rejected)
    { Weight(output); Weight(reduction); Weight(rejected); Require(output>0 && input==output+reduction+rejected,"Input must equal output + process reduction + rejected kg."); }
    public static decimal Packaging(decimal weight,int count,decimal loss)
    { Weight(weight); Weight(loss); Require(weight>0 && count>0,"Pack size and count must be positive."); return checked(weight*count+loss); }
    public static void Available(decimal stock,decimal requested) => Require(requested>0 && stock>=requested,"Insufficient available stock.",409);
}
