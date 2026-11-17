using System;
using System.IO;
using System.Reflection;

namespace Petal.Shell;

/// <summary>
/// The libraries beside this one (Svg.NET and what it needs) found by name. Windows loads this library into a
/// process of its own (the thumbnail or preview host), which has no configuration to say which version of a
/// library stands for another: when one asks for a version that isn't the one here, the one here is given.
/// </summary>
internal static class Libraries
{
    private static bool _guarded;

    public static void Guard()
    {
        if (_guarded)
            return;
        _guarded = true;
        var here = Path.GetDirectoryName(typeof(Libraries).Assembly.Location) ?? "";
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var name = new AssemblyName(args.Name).Name;
            var path = Path.Combine(here, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
    }
}
