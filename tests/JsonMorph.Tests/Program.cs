using System.Text.Json;
using System.Text.Json.Nodes;
using JsonMorph.Core;

var fixedNow = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
var engine = new MappingEngine(() => fixedNow);
var t = new TestRunner();

MappingDefinition M(string json) => JsonSerializer.Deserialize<MappingDefinition>(json, JsonDefaults.Options)!;
JsonNode J(string json) => JsonNode.Parse(json)!;
JsonNode? Apply(string value, string spec) => Transforms.Apply(J(value), J(spec));

// ------------------------------------------------------------- JsonPath
t.Test("path: nested property, index, negative index", () =>
{
    var doc = J("""{"a":{"b":[1,2,3]},"odd key":{"x":true}}""");
    t.Eq("2", JsonPath.Parse("$.a.b[1]").Select(doc).Single()!.ToJsonString());
    t.Eq("3", JsonPath.Parse("a.b[-1]").Select(doc).Single()!.ToJsonString());
    t.Eq("true", JsonPath.Parse("$['odd key'].x").Select(doc).Single()!.ToJsonString());
});

t.Test("path: wildcards over arrays and objects", () =>
{
    var doc = J("""{"items":[{"id":1},{"id":2},{"no":3}],"m":{"a":1,"b":2}}""");
    t.Eq("1,2", string.Join(",", JsonPath.Parse("$.items[*].id").Select(doc).Select(n => n!.ToJsonString())));
    t.Eq(2, JsonPath.Parse("$.m.*").Select(doc).Count());
    t.True(JsonPath.Parse("$.items[*].id").IsMulti);
});

t.Test("path: invalid paths are rejected", () =>
{
    t.False(JsonPath.TryParse("$.a[", out _, out _));
    t.False(JsonPath.TryParse("$.a..b", out _, out _));
    t.False(JsonPath.TryParse("$.a[x]", out _, out _));
});

t.Test("path: SetValue builds objects and arrays", () =>
{
    var o = new JsonObject();
    JsonPath.Parse("a.b.c").SetValue(o, 1);
    JsonPath.Parse("a.list[2].name").SetValue(o, "x");
    t.Eq("""{"a":{"b":{"c":1},"list":[null,null,{"name":"x"}]}}""", o.ToJsonString());
});

// ------------------------------------------------------------- Transforms
t.Test("transforms: string ops", () =>
{
    t.Eq("\"ABC\"", Apply("\"  ABC \"", "\"trim\"")!.ToJsonString());
    t.Eq("\"HELLO\"", Apply("\"hello\"", "\"upper\"")!.ToJsonString());
    t.Eq("\"id-7\"", Apply("7", """{"op":"prefix","value":"id-"}""")!.ToJsonString());
    t.Eq("\"5551234567\"", Apply("\"555-123-4567\"", """{"op":"replace","find":"-","with":""}""")!.ToJsonString());
    t.Eq("\"******4567\"", Apply("\"5551234567\"", """{"op":"mask","keepLast":4}""")!.ToJsonString());
    t.Eq("\"2024\"", Apply("\"order-2024-x\"", """{"op":"regex","pattern":"(\\d+)"}""")!.ToJsonString());
    t.Null(Apply("\"   \"", "\"nullIfEmpty\""));
});

t.Test("transforms: numbers and booleans", () =>
{
    t.Eq("19.99", Apply("\"19.99\"", "\"toNumber\"")!.ToJsonString());
    t.Eq("42", Apply("\"42\"", "\"toNumber\"")!.ToJsonString());
    t.Eq("3", Apply("3.9", "\"toInt\"")!.ToJsonString());
    t.Eq("2.35", Apply("2.345", """{"op":"round","digits":2}""")!.ToJsonString());
    t.Eq("250", Apply("2.5", """{"op":"multiply","by":100}""")!.ToJsonString());
    t.Eq("true", Apply("\"yes\"", "\"toBool\"")!.ToJsonString());
    t.Eq("false", Apply("0", "\"toBool\"")!.ToJsonString());
    t.Throws<MappingException>(() => Apply("\"abc\"", "\"toNumber\""));
    t.Throws<MappingException>(() => Apply("\"maybe\"", "\"toBool\""));
});

t.Test("transforms: dates", () =>
{
    t.Eq("\"2021-03-15\"", Apply("\"03/15/2021\"", """{"op":"date","from":"MM/dd/yyyy","to":"yyyy-MM-dd"}""")!.ToJsonString());
    t.Eq("\"2023-11-14T22:13:20Z\"", Apply("1700000000", """{"op":"date","from":"unix","to":"yyyy-MM-ddTHH:mm:ssZ"}""")!.ToJsonString());
    t.Eq("1700000000", Apply("\"2023-11-14T22:13:20Z\"", """{"op":"date","to":"unix"}""")!.ToJsonString());
    t.Eq("\"2020-01-31\"", Apply("\"31.01.2020\"", """{"op":"date","from":["MM/dd/yyyy","dd.MM.yyyy"],"to":"yyyy-MM-dd"}""")!.ToJsonString());
    t.Throws<MappingException>(() => Apply("\"nope\"", """{"op":"date"}"""));
});

t.Test("transforms: arrays (element-wise vs whole-value)", () =>
{
    t.Eq("""["a","b"]""", Apply("\"a, b,\"", """{"op":"split"}""")!.ToJsonString());
    t.Eq("""["A","B"]""", Apply("""["a","b"]""", "\"upper\"")!.ToJsonString());
    t.Eq("\"a|b\"", Apply("""["a","b"]""", """{"op":"join","sep":"|"}""")!.ToJsonString());
    t.Eq("3", Apply("[1,2,3]", "\"count\"")!.ToJsonString());
    t.Eq("[1,2]", Apply("[1,2,1,2]", "\"distinct\"")!.ToJsonString());
    t.Eq("[1]", Apply("[null,1,null]", "\"compact\"")!.ToJsonString());
    t.Eq("1", Apply("[1,2]", "\"first\"")!.ToJsonString());
});

t.Test("transforms: map, hash, json", () =>
{
    t.Eq("\"ACTIVE\"", Apply("true", """{"op":"map","values":{"true":"ACTIVE"}}""")!.ToJsonString());
    t.Eq("\"OTHER\"", Apply("\"x\"", """{"op":"map","values":{"a":1},"fallback":"OTHER"}""")!.ToJsonString());
    t.Eq(64, Apply("\"secret\"", "\"hash\"")!.GetValue<string>().Length);
    t.Eq("""{"a":1}""", Apply("\"{\\\"a\\\":1}\"", "\"json\"")!.ToJsonString());
    t.Eq("\"a+b é\"", JsonValue.Create("a+b é").ToJsonString(JsonDefaults.Options));
});

// ------------------------------------------------------------- Engine
t.Test("engine: renames, nesting, template, const, $now", () =>
{
    var r = engine.Map(J("""{"fn":"Ada","ln":"Lovelace","x":{"y":5}}"""), M("""
        {"rules":[
          {"target":"name.full","template":"{$.fn} {$.ln}"},
          {"target":"value","source":"$.x.y"},
          {"target":"kind","const":"person"},
          {"target":"at","source":"$now"}
        ]}
        """));
    t.True(r.Success);
    t.Eq("""{"name":{"full":"Ada Lovelace"},"value":5,"kind":"person","at":"2026-01-02T03:04:05.0000000Z"}""", r.Output.ToJsonString());
});

t.Test("engine: coalesce, default, required, when", () =>
{
    var mapping = M("""
        {"rules":[
          {"target":"email","sources":["$.work_email","$.home_email"],"required":true},
          {"target":"tier","source":"$.tier","default":"free"},
          {"target":"vip","const":true,"when":"$.flags.vip"},
          {"target":"guest","const":true,"when":"!$.account"}
        ]}
        """);
    var ok = engine.Map(J("""{"home_email":"a@b.c","flags":{"vip":"yes"},"account":{"id":1}}"""), mapping);
    t.True(ok.Success);
    t.Eq("""{"email":"a@b.c","tier":"free","vip":true}""", ok.Output.ToJsonString());

    var bad = engine.Map(J("{}"), mapping);
    t.False(bad.Success);
    t.Eq("email", bad.Errors.Single().Target);
    t.Eq("""{"tier":"free","guest":true}""", bad.Output.ToJsonString());
});

t.Test("engine: wildcard source collects values", () =>
{
    var r = engine.Map(J("""{"lines":[{"sku":"a"},{"sku":"b"}]}"""), M("""{"rules":[{"target":"skus","source":"$.lines[*].sku","transform":["upper"]}]}"""));
    t.Eq("""{"skus":["A","B"]}""", r.Output.ToJsonString());
});

t.Test("engine: each maps nested arrays and reports indexed errors", () =>
{
    var r = engine.Map(J("""{"orders":[{"no":"1","amt":"2.5"},{"amt":"x"}]}"""), M("""
        {"rules":[{"target":"orders","source":"$.orders","each":{"rules":[
          {"target":"id","source":"$.no","required":true},
          {"target":"total","source":"$.amt","transform":["toNumber"]}
        ]}}]}
        """));
    t.Eq("""{"orders":[{"id":"1","total":2.5},{}]}""", r.Output.ToJsonString());
    t.Eq("orders[1].id;orders[1].total", string.Join(";", r.Errors.Select(e => e.Target)));
});

t.Test("engine: includeNulls writes explicit nulls", () =>
{
    var r = engine.Map(J("{}"), M("""{"includeNulls":true,"rules":[{"target":"a","source":"$.missing"}]}"""));
    t.Eq("""{"a":null}""", r.Output.ToJsonString());
});

t.Test("engine: sample mapping over sample data", () =>
{
    var mapping = M(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "users-v1-to-v2.mapping.json")));
    t.Eq(0, MappingValidator.Validate(mapping, requireName: true).Count);
    var users = J(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "legacy-users.json"))).AsArray();

    var jane = engine.Map(users[0], mapping);
    t.True(jane.Success);
    t.Eq("""
        {"id":"101","profile":{"fullName":"Jane Doe","email":"jane.doe@example.com","phoneMasked":"******4567"},"status":"ACTIVE","createdAt":"2021-03-15","tags":["vip","beta"],"orders":[{"orderId":"A-1","total":19.99,"placedAt":"2023-11-14T22:13:20Z"},{"orderId":"A-2","total":5.5,"placedAt":"2023-11-15T22:13:20Z"}],"meta":{"migratedAt":"2026-01-02T03:04:05.0000000Z","source":"legacy-crm"}}
        """.Trim(), jane.Output.ToJsonString());

    var sam = engine.Map(users[1], mapping);
    t.True(sam.Success);
    t.Eq("INACTIVE", sam.Output["status"]!.GetValue<string>());

    var broken = engine.Map(users[2], mapping);
    t.False(broken.Success);
    t.Eq("profile.email;status;createdAt;orders[0].orderId;orders[0].total",
        string.Join(";", broken.Errors.Select(e => e.Target)));
});

// ------------------------------------------------------------- Validator
t.Test("validator: catches bad rules", () =>
{
    var errors = MappingValidator.Validate(M("""
        {"name":"../evil","rules":[
          {"target":"","source":"$.a"},
          {"target":"x[*]","source":"$.a"},
          {"target":"y","source":"$.a","const":1},
          {"target":"z","source":"$.a[","transform":["nope",{"op":"map"}]}
        ]}
        """), requireName: true);
    t.Eq(7, errors.Count);
    t.Eq(1, MappingValidator.Validate(M("""{"rules":[]}""")).Count);
});

// ------------------------------------------------------------- Inferrer
t.Test("inferrer: learns A->B from one example and reproduces it", () =>
{
    var a = J("""
        {"user_id":101,"first":"Jane","last":"Doe","contact":{"email":"  Jane.Doe@Example.COM "},
         "age":"34","labels":"vip,beta",
         "orders":[{"order_no":"A-1","amount":19.99},{"order_no":"A-2","amount":5}]}
        """);
    var b = J("""
        {"id":"101","profile":{"fullName":"Jane Doe","email":"jane.doe@example.com","age":34},
         "tags":["vip","beta"],
         "orders":[{"orderId":"A-1","total":19.99}],
         "unknownField":"zzz"}
        """);

    var result = MappingInferrer.Infer(a, b, "inferred");
    var output = engine.Map(a, result.Mapping).Output;

    t.Eq("unknownField", string.Join(",", result.Unmatched));
    t.Eq("101", output["id"]!.GetValue<string>());
    t.Eq("Jane Doe", output["profile"]!["fullName"]!.GetValue<string>());
    t.Eq("jane.doe@example.com", output["profile"]!["email"]!.GetValue<string>());
    t.Eq("34", output["profile"]!["age"]!.ToJsonString());
    t.Eq("""["vip","beta"]""", output["tags"]!.ToJsonString());
    // "each" generalizes to every order, not just the one in the example.
    t.Eq("""[{"orderId":"A-1","total":19.99},{"orderId":"A-2","total":5}]""", output["orders"]!.ToJsonString());
    t.Eq(0, MappingValidator.Validate(result.Mapping).Count);
});

t.Test("inferrer: exact reproduction when nothing is unmatched", () =>
{
    var a = J("""{"a":{"b":"X"},"n":"5"}""");
    var b = J("""{"flat":"x","num":5}""");
    var result = MappingInferrer.Infer(a, b);
    t.True(JsonNode.DeepEquals(engine.Map(a, result.Mapping).Output, b));
});

return t.Report();

internal sealed class TestRunner
{
    private int _passed, _failed;

    public void Test(string name, Action body)
    {
        try
        {
            body();
            _passed++;
            Console.WriteLine($"  PASS  {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  FAIL  {name}\n        {ex.GetType().Name}: {ex.Message}");
        }
    }

    public int Report()
    {
        Console.WriteLine($"\n{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    public void Eq<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"expected <{expected}> but got <{actual}>");
    }

    public void True(bool v) { if (!v) throw new Exception("expected true"); }
    public void False(bool v) { if (v) throw new Exception("expected false"); }
    public void Null(object? v) { if (v is not null) throw new Exception($"expected null but got {v}"); }

    public void Throws<TEx>(Action a) where TEx : Exception
    {
        try { a(); }
        catch (TEx) { return; }
        throw new Exception($"expected {typeof(TEx).Name}");
    }
}
