using System;
using System.Linq;
using System.Reflection;

class Program
{
    static void Main()
    {
        var megaPath = @"C:\Users\S\.nuget\packages\megaapiclient\1.10.5\lib\netstandard2.0\MegaApiClient.dll";
        var winfspPath = @"C:\Users\S\.nuget\packages\winfsp.net\2.2.26183\lib\netstandard2.0\winfsp-msil.dll";
        Console.WriteLine("MEGA TYPES");
        var a = Assembly.LoadFrom(megaPath);
        foreach (var t in a.GetTypes().Where(t => t.FullName != null && t.FullName.Contains("Mega")))
            Console.WriteLine(t.FullName);
        Console.WriteLine("---");
        Console.WriteLine("WINFSP TYPES");
        var b = Assembly.LoadFrom(winfspPath);
        foreach (var t in b.GetTypes().Where(t => t.FullName != null && (t.FullName.Contains("Fsp") || t.FullName.Contains("FileSystem"))))
            Console.WriteLine(t.FullName);
    }
}
