using TargetLib;

Console.WriteLine("[ROUTE_GROUP_APP] TargetLib version: " + VersionReport.GetVersion());
Console.WriteLine("[ROUTE_GROUP_APP] CommonUtils (via TargetLib): " + VersionReport.GetCommonUtilsVersion());
Console.WriteLine("[ROUTE_GROUP_APP] CommonUtils (direct): " + CommonUtils.Helper.GetVersion());
Console.WriteLine("[ROUTE_GROUP_APP] Done.");
