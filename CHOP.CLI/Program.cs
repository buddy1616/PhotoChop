using Chop.Core;
using Chop.Rendering;

if (args.Length < 2 || args[0] != "run")
{
    Console.Error.WriteLine("Usage: chop run script.json [name=JSON ...]");
    return 2;
}

try
{
    var parameters = new Dictionary<string, string>();
    foreach (string assignment in args.Skip(2))
    {
        var parts = assignment.Split('=', 2);
        if (parts.Length != 2 || parts[0].Length == 0)
            throw new ArgumentException("Parameters use name=JSON.");
        parameters.Add(parts[0], parts[1]);
    }
    using var script = File.OpenRead(args[1]);
    Scripts.Run(new CommandEngine(new SkiaRenderer()), script, parameters);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
