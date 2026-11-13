using System.Reflection;

var marker = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
    .Single(attribute => attribute.Key == "DummyMarker").Value!;
File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "started.log"), marker + Environment.NewLine);
return marker == "failed" ? 7 : 0;
