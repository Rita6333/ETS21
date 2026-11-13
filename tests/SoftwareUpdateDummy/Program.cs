using System.Reflection;

var marker = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
    .Single(attribute => attribute.Key == "DummyMarker").Value!;
File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "started.log"), marker + Environment.NewLine);
if (marker == "failed") return 7;
if (marker == "no-window")
{
    Thread.Sleep(TimeSpan.FromSeconds(30));
    return 0;
}
if (args.Length == 2 && args[0] == "--update-relaunch" &&
    Guid.TryParseExact(args[1], "N", out var token))
{
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "relaunch-" + token.ToString("N") + ".ready"), token.ToString("N"));
    Thread.Sleep(TimeSpan.FromSeconds(1));
}
return 0;
