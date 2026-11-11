using System.Text.Json;
using ApiConformance;
using ApiConformance.Cli;

var protocol = Console.Out;
Console.SetOut(TextWriter.Null);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

while (Console.In.ReadLine() is { } line)
{
    if (line.Length == 0) continue;
    var response = await Handler.Handle(line, json);
    protocol.WriteLine(JsonSerializer.Serialize(response, json));
    protocol.Flush();
}
