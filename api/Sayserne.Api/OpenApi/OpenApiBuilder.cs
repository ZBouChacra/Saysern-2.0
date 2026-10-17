using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Sayserne.Api.Auth;

namespace Sayserne.Api.OpenApi;

/// <summary>
/// Describes the public API (everything under /api except the back office) as an OpenAPI 3.0 document, read from the running route table, so it can never drift from the code:
/// paths, methods, path/query parameters, request bodies (from the request records), who may call it, and which rate limit applies.
/// Response bodies are not typed (handlers return untyped results): the shapes are those of web/src/api.ts. No external package is needed.
/// </summary>
public static partial class OpenApiBuilder
{
    public const string ApiVersion = "1.0";

    [GeneratedRegex(@"\{\*?(\w+)(:[^}]*)?\??\}")]
    private static partial Regex RouteParameter();

    public static JsonObject Build(EndpointDataSource source)
    {
        var components = new JsonObject();
        var paths = new SortedDictionary<string, SortedDictionary<string, JsonObject>>(StringComparer.Ordinal);

        foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
        {
            var raw = endpoint.RoutePattern.RawText ?? "";
            if (!raw.StartsWith("/api/", StringComparison.Ordinal) || raw.StartsWith("/api/admin", StringComparison.Ordinal) || raw == "/api/openapi.json") continue;
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Where(m => m is not ("OPTIONS" or "HEAD")).ToList() ?? [];
            var path = RouteParameter().Replace(raw, "{$1}").TrimEnd('/');      // a route group with an empty route ends in "/"
            foreach (var method in methods)
            {
                if (!paths.TryGetValue(path, out var byMethod)) paths[path] = byMethod = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
                byMethod[method.ToLowerInvariant()] = Operation(endpoint, method, path, components);
            }
        }

        var pathsNode = new JsonObject();
        foreach (var (path, byMethod) in paths)
        {
            var item = new JsonObject();
            foreach (var (method, op) in byMethod) item[method] = op;
            pathsNode[path] = item;
        }
        components["Error"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "What every failed request returns. `error` is a stable code (the web app shows a translated text for it); `errors` maps field names to messages for validation failures.",
            ["required"] = new JsonArray("error"),
            ["properties"] = new JsonObject { ["error"] = new JsonObject { ["type"] = "string", ["example"] = "VALIDATION_FAILED" }, ["errors"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } } },
        };

        // components.schemas in a stable order
        var schemas = new JsonObject();
        foreach (var name in components.Select(p => p.Key).OrderBy(n => n, StringComparer.Ordinal).ToList())
        {
            var clone = components[name]!.DeepClone().AsObject();
            clone.Remove("x-clr");
            schemas[name] = clone;
        }

        return new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject
            {
                ["title"] = "SaySerné API",
                ["version"] = ApiVersion,
                ["description"] = "The public API used by the web site (and the mobile app). Generated from the code.\n\n" +
                    "**Signing in.** `POST /api/auth/login` (or register / Google) sets the `sayserne.auth` cookie; send it with every request. " +
                    "**Cross-site protection.** Every request that changes data (anything but GET) must carry an `Origin` header the API allows, or the header `X-Requested-With: sayserne-web`; otherwise it is refused with `CSRF_REJECTED`. A native app has no `Origin`, so it sends the header.\n\n" +
                    "**Errors** use one shape (see `Error`). **Responses** are JSON unless noted; their exact shapes are the types in `web/src/api.ts`. Times are UTC ISO 8601. " +
                    "Business limits (lengths, counts, durations) are runtime settings published by `GET /api/config/public`; texts and lists come from `/api/translations/{language}` and `/api/lookups/{type}`.",
            },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = "/", ["description"] = "The same host that serves the site" }),
            ["paths"] = pathsNode,
            ["components"] = new JsonObject
            {
                ["schemas"] = schemas,
                ["securitySchemes"] = new JsonObject { ["sessionCookie"] = new JsonObject { ["type"] = "apiKey", ["in"] = "cookie", ["name"] = "sayserne.auth", ["description"] = "Set by the sign-in endpoints." } },
            },
        };
    }

    public static string Serialize(JsonObject doc) => doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("\r\n", "\n") + "\n";

    private static JsonObject Operation(RouteEndpoint endpoint, string method, string path, JsonObject components)
    {
        var handler = endpoint.Metadata.GetMetadata<MethodInfo>();
        var pathNames = endpoint.RoutePattern.Parameters.Select(p => p.Name).ToHashSet();
        var parameters = new JsonArray();
        JsonObject? body = null;
        var hasBodyVerb = method is "POST" or "PUT" or "PATCH" or "DELETE";
        var nullability = new NullabilityInfoContext();

        foreach (var p in handler?.GetParameters() ?? [])
        {
            if (p.GetCustomAttribute<Microsoft.AspNetCore.Http.AsParametersAttribute>() is not null)      // [AsParameters] Filters f: every property is its own query parameter
            {
                var ctor = p.ParameterType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
                foreach (var c in ctor.GetParameters().Where(c => IsSimple(c.ParameterType)))
                    parameters.Add(new JsonObject
                    {
                        ["name"] = JsonNamingPolicy.CamelCase.ConvertName(c.Name!), ["in"] = "query",
                        ["required"] = !(c.HasDefaultValue || nullability.Create(c).ReadState == NullabilityState.Nullable || Nullable.GetUnderlyingType(c.ParameterType) is not null),
                        ["schema"] = Schema(c.ParameterType, components),
                    });
                continue;
            }
            if (IsFrameworkOrService(p.ParameterType)) continue;
            var optional = p.HasDefaultValue || nullability.Create(p).ReadState == NullabilityState.Nullable || Nullable.GetUnderlyingType(p.ParameterType) is not null;
            if (p.Name is { } name && pathNames.Contains(name))
                parameters.Add(new JsonObject { ["name"] = name, ["in"] = "path", ["required"] = true, ["schema"] = Schema(p.ParameterType, components) });
            else if (IsSimple(p.ParameterType))
                parameters.Add(new JsonObject { ["name"] = p.Name, ["in"] = "query", ["required"] = !optional, ["schema"] = Schema(p.ParameterType, components) });
            else if (hasBodyVerb && IsRecord(p.ParameterType))
                body = new JsonObject { ["required"] = !optional, ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = Schema(p.ParameterType, components) } } };
        }

        // the one endpoint that reads a multipart upload itself: its query parameters were found above; the file part is described here
        if (method == "POST" && path == "/api/files")
            body = new JsonObject { ["required"] = true, ["content"] = new JsonObject { ["multipart/form-data"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "object", ["required"] = new JsonArray("file"), ["properties"] = new JsonObject { ["file"] = new JsonObject { ["type"] = "string", ["format"] = "binary" } } } } } };

        var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        var needsSession = authorize.Count > 0 && !anonymous;
        var limit = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;

        var op = new JsonObject
        {
            ["operationId"] = OperationId(method, path),
            ["tags"] = new JsonArray(path.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "api"),
            ["summary"] = $"{method} {path}",
            ["description"] = needsSession ? "Requires a signed-in session." : "Public: no sign-in needed.",
        };
        if (parameters.Count > 0) op["parameters"] = parameters;
        if (body is not null) op["requestBody"] = body;
        if (needsSession) op["security"] = new JsonArray(new JsonObject { ["sessionCookie"] = new JsonArray() });
        if (limit is not null) op["x-rate-limit-policy"] = limit;
        op["responses"] = new JsonObject
        {
            ["2XX"] = new JsonObject { ["description"] = "Success (JSON body unless the endpoint streams a file or returns 204)." },
            ["default"] = new JsonObject { ["description"] = "An error.", ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = new JsonObject { ["$ref"] = "#/components/schemas/Error" } } } },
        };
        return op;
    }

    private static string OperationId(string method, string path)
    {
        var words = Regex.Replace(path, @"[^A-Za-z0-9]+", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return method.ToLowerInvariant() + string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private static bool IsFrameworkOrService(Type t) =>
        t == typeof(CancellationToken) || t == typeof(ClaimsPrincipal) || typeof(HttpContext).IsAssignableFrom(t) || t == typeof(HttpRequest) || t == typeof(HttpResponse) ||
        t.IsInterface || (!IsSimple(t) && !IsRecord(t) && !IsCollection(t));      // dependencies the framework injects: interfaces and ordinary classes

    private static bool IsSimple(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(DateOnly) || t == typeof(DateTimeOffset) || t == typeof(Guid);
    }

    private static bool IsRecord(Type t) => t.IsClass && t.GetMethod("<Clone>$") is not null;
    private static bool IsCollection(Type t) => t != typeof(string) && (t.IsArray || t.GetInterfaces().Append(t).Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)));

    private static JsonObject Schema(Type type, JsonObject components)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        var t = underlying ?? type;
        var schema = SchemaCore(t, components);
        if (underlying is not null && schema["$ref"] is null) schema["nullable"] = true;
        return schema;
    }

    private static JsonObject SchemaCore(Type t, JsonObject components)
    {
        if (t == typeof(string)) return new JsonObject { ["type"] = "string" };
        if (t == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (t == typeof(long) || t == typeof(ulong)) return new JsonObject { ["type"] = "integer", ["format"] = "int64" };
        if (t == typeof(int) || t == typeof(short) || t == typeof(byte) || t == typeof(uint) || t == typeof(ushort)) return new JsonObject { ["type"] = "integer", ["format"] = "int32" };
        if (t == typeof(decimal) || t == typeof(double) || t == typeof(float)) return new JsonObject { ["type"] = "number" };
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        if (t == typeof(DateOnly)) return new JsonObject { ["type"] = "string", ["format"] = "date" };
        if (t == typeof(Guid)) return new JsonObject { ["type"] = "string", ["format"] = "uuid" };
        if (t.IsEnum) return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Enum.GetNames(t).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()) };
        if (t == typeof(JsonElement) || t == typeof(object) || t == typeof(JsonNode)) return new JsonObject();
        if (t.IsGenericType && t.GetGenericTypeDefinition() is var g && (g == typeof(Dictionary<,>) || g == typeof(IReadOnlyDictionary<,>) || g == typeof(IDictionary<,>)))
            return new JsonObject { ["type"] = "object", ["additionalProperties"] = Schema(t.GetGenericArguments()[1], components) };
        if (IsCollection(t))
        {
            var item = t.IsArray ? t.GetElementType()! : t.GetInterfaces().Append(t).First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)).GetGenericArguments()[0];
            return new JsonObject { ["type"] = "array", ["items"] = Schema(item, components) };
        }
        if (!t.IsClass) return new JsonObject();

        var name = t.Name;
        if (components[name] is JsonObject taken && taken["x-clr"]?.GetValue<string>() != t.FullName)
            name = (t.DeclaringType?.Name ?? "") + t.Name;           // another type already has this short name
        if (components[name] is null)
        {
            components[name] = new JsonObject { ["x-clr"] = t.FullName };   // reserve first: a type that refers to itself must not recurse for ever (the marker is removed again at the end)
            var props = new JsonObject(); var required = new JsonArray();
            var ctor = t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            var ctorParams = ctor?.GetParameters().ToDictionary(p => p.Name!, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, ParameterInfo>();
            var nullability = new NullabilityInfoContext();
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(p => p.Name != "EqualityContract").OrderBy(p => p.MetadataToken))
            {
                var json = JsonNamingPolicy.CamelCase.ConvertName(p.Name);
                props[json] = Schema(p.PropertyType, components);
                var isNullable = nullability.Create(p).ReadState == NullabilityState.Nullable || Nullable.GetUnderlyingType(p.PropertyType) is not null;
                if (!isNullable && !(ctorParams.TryGetValue(p.Name, out var cp) && cp.HasDefaultValue)) required.Add(json);
            }
            var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
            if (required.Count > 0) schema["required"] = required;
            schema["x-clr"] = t.FullName;
            components[name] = schema;
        }
        return new JsonObject { ["$ref"] = $"#/components/schemas/{name}" };
    }
}
