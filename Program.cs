using System.Collections;
using System.ComponentModel.Design;
using System.Resources;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResxMcp;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task Main(string[] args)
    {
       
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);
        var tools = new[]
        {
            new ToolDesc("resx.read",        "Read .resx as UTF-8 text",                     new { file = "string" }),
            new ToolDesc("resx.write",       "Write UTF-8 text to .resx (atomic replace)",   new { file = "string", content = "string", backup = "boolean?" }),
            new ToolDesc("resx.setEntry",    "Set or add a key in .resx",                    new { file = "string", name = "string", value = "string", comment = "string?", backup = "boolean?" }),
            new ToolDesc("resx.removeEntry", "Remove a key from .resx",                      new { file = "string", name = "string", backup = "boolean?" }),
        };

        bool first = true; // ★ 仅第一次剥 BOM

        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            if (first && line.Length > 0 && line[0] == '\uFEFF')
                line = line.TrimStart('\uFEFF'); // ★ 去掉 UTF-8 BOM
            first = false;

            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (Exception ex) { WriteError(null, -32700, $"Parse error: {ex.Message}"); continue; }


            using (doc)
            {
                var root = doc.RootElement;

                // 可靠地提取 id（字符串或数字都支持；必须原样回传）
                object? idObj = null;
                if (root.TryGetProperty("id", out var idEl))
                {
                    if (idEl.ValueKind == JsonValueKind.String) idObj = idEl.GetString();
                    else if (idEl.ValueKind == JsonValueKind.Number)
                    {
                        if (idEl.TryGetInt64(out var n)) idObj = n; else idObj = idEl.GetDouble();
                    }
                }

                if (!root.TryGetProperty("method", out var mEl) || mEl.ValueKind != JsonValueKind.String)
                {
                    WriteError(idObj, -32600, "Invalid Request: missing method");
                    continue;
                }

                var method = mEl.GetString();

                if (idObj is null && method is not null && method.StartsWith("notifications/", StringComparison.Ordinal))
                    continue;

                try
                {
                    switch (method)
                    {
                        case "initialize":
                            WriteResult(idObj, new
                            {
                                protocolVersion = "2024-11-05",
                                serverInfo = new { name = "ResxMcp", version = "1.0.0" },
                                capabilities = new { tools = new { listChanged = false } }
                            });
                            break;

                        case "tools/list":
                            WriteResult(idObj, new { tools = tools.Select(t => t.ToListItem()) });
                            break;

                        case "tools/call":
                            {
                                var p = root.GetProperty("params");
                                var name = p.GetProperty("name").GetString();
                                var toolArgs = p.GetProperty("arguments");

                                switch (name)
                                {
                                    case "resx.read":
                                        {
                                            var file = toolArgs.GetProperty("file").GetString()!;
                                            var text = await File.ReadAllTextAsync(file, new UTF8Encoding(false));
                                            WriteToolResult(idObj, text);
                                            break;
                                        }
                                    case "resx.write":
                                        {
                                            var file = toolArgs.GetProperty("file").GetString()!;
                                            var content = toolArgs.GetProperty("content").GetString()!;
                                            var backup = toolArgs.TryGetProperty("backup", out var be) && be.GetBoolean();

                                            if (backup && File.Exists(file))
                                                File.Copy(file, file + ".bak", true);

                                            var tmp = file + ".mcp.tmp";
                                            await File.WriteAllTextAsync(tmp, NormalizeLF(content), new UTF8Encoding(false));
                                            File.Move(tmp, file, true);
                                            WriteToolResult(idObj, $"Wrote UTF-8 text to {file}.");
                                            break;
                                        }
                                    case "resx.setEntry":
                                        {
                                            var file = toolArgs.GetProperty("file").GetString()!;
                                            var key = toolArgs.GetProperty("name").GetString()!;
                                            var val = toolArgs.GetProperty("value").GetString()!;
                                            var cmt = toolArgs.TryGetProperty("comment", out var ce) ? ce.GetString() : null;
                                            var backup = toolArgs.TryGetProperty("backup", out var be) && be.GetBoolean();

                                            SetResxEntry(file, key, val, cmt, backup);
                                            WriteToolResult(idObj, $"Updated key '{key}' in {file}.");
                                            break;
                                        }
                                    case "resx.removeEntry":
                                        {
                                            var file = toolArgs.GetProperty("file").GetString()!;
                                            var key = toolArgs.GetProperty("name").GetString()!;
                                            var backup = toolArgs.TryGetProperty("backup", out var be) && be.GetBoolean();
                                            RemoveResxEntry(file, key, backup);
                                            WriteToolResult(idObj, $"Removed key '{key}' from {file}.");
                                            break;
                                        }
                                    default:
                                        WriteError(idObj, -32601, $"Unknown tool: {name}");
                                        break;
                                }
                                break;
                            }

                        // （可选）兼容 ping
                        case "ping":
                            WriteResult(idObj, new { ok = true });
                            break;

                        default:
                            WriteError(idObj, -32601, $"Method not found: {method}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    WriteError(idObj, -32000, ex.Message);
                }
            }
        }
    }

    // ---------- .resx 语义读写 ----------
    private static void SetResxEntry(string path, string name, string value, string? comment, bool backup = false)
    {
        var dict = ReadResxToDict(path);
        dict[name] = (value, comment);
        WriteDictToResx(path, dict, backup);
    }

    private static void RemoveResxEntry(string path, string name, bool backup = false)
    {
        var dict = ReadResxToDict(path);
        if (dict.Remove(name))
            WriteDictToResx(path, dict, backup);
    }

    private static Dictionary<string, (string val, string? comment)> ReadResxToDict(string path)
    {
        var result = new Dictionary<string, (string, string?)>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;

        using var reader = new ResXResourceReader(path) { UseResXDataNodes = true };
        foreach (DictionaryEntry de in reader)
        {
            var node = (ResXDataNode)de.Value!;
            var val = node.GetValue((ITypeResolutionService?)null)?.ToString() ?? "";
            result[node.Name] = (val, node.Comment);
        }
        return result;
    }

    private static void WriteDictToResx(string path, Dictionary<string, (string val, string? comment)> dict, bool backup)
    {
        var tmp = path + ".mcp.tmp";
        using (var writer = new ResXResourceWriter(tmp))
        {
            foreach (var kv in dict.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var node = new ResXDataNode(kv.Key, kv.Value.val) { Comment = kv.Value.comment };
                writer.AddResource(node);
            }
        }
        // 默认不生成 .bak，仅在 backup=true 时备份
        if (backup && File.Exists(path)) File.Copy(path, path + ".bak", true);
        File.Move(tmp, path, true);
    }

    private static string NormalizeLF(string s) => s.Replace("\r\n", "\n");

    // ---------- JSON-RPC 输出（严格 echo id） ----------
    private static void WriteResult(object? id, object result)
    {
        var obj = new { jsonrpc = "2.0", id, result };
        Console.WriteLine(JsonSerializer.Serialize(obj, JsonOpts));
    }

    private static void WriteError(object? id, int code, string message)
    {
        var obj = new { jsonrpc = "2.0", id, error = new { code, message } };
        Console.WriteLine(JsonSerializer.Serialize(obj, JsonOpts));
    }

    private static void WriteToolResult(object? id, string text)
    {
        var obj = new
        {
            jsonrpc = "2.0",
            id,
            result = new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text
                    }
                },
                isError = false
            }
        };
        Console.WriteLine(JsonSerializer.Serialize(obj, JsonOpts));
    }

    private readonly record struct ToolDesc(string Name, string Description, object Schema)
    {
        public object ToListItem() => new
        {
            name = Name,
            description = Description,
            inputSchema = BuildInputSchema()
        };

        private object BuildInputSchema()
        {
            var properties = new Dictionary<string, object>();
            var required = new List<string>();

            foreach (var property in Schema.GetType().GetProperties())
            {
                var value = property.GetValue(Schema)?.ToString();
                var isRequired = value is not null && !value.EndsWith("?", StringComparison.Ordinal);
                var type = value?.TrimEnd('?') switch
                {
                    "boolean" => "boolean",
                    _ => "string"
                };

                properties[property.Name] = new { type };
                if (isRequired)
                    required.Add(property.Name);
            }

            return new { type = "object", properties, required, additionalProperties = false };
        }
    }
}
