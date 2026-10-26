using System.Text.Json;
using EvalRunner;

var protocol = Console.Out;
Console.SetOut(TextWriter.Null);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

while (Console.In.ReadLine() is { } line)
{
    if (line.Length == 0) continue;
    var request = JsonSerializer.Deserialize<Request>(line, json)!;
    Result result;
    try { result = await SampleRunner.Run(request); }
    catch (Exception e) { result = SampleRunner.Failure(request.Id, $"{e.GetType().Name}: {e.Message}"); }
    protocol.WriteLine(JsonSerializer.Serialize(result, json));
    protocol.Flush();
    if (SampleRunner.Poisoned) Environment.Exit(3);
}
