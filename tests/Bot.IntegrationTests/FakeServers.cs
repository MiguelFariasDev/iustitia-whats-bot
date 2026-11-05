using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Advocacia.Bot.IntegrationTests;

/// <summary>
/// O Core, do ponto de vista do Bot: implementa o contrato HTTP de <c>/api/v1/bot/atendimentos</c>
/// — inclusive a regra que importa (o bot não fala com uma conversa em <c>Human</c>) — e registra
/// tudo o que recebeu, na ordem.
/// </summary>
public sealed class FakeCoreServer : HttpMessageHandler
{
    public const string ValidKey = "adv_bot_chave_do_escritorio";

    private readonly object _gate = new();
    private readonly Dictionary<string, Guid> _byExternalId = [];
    private readonly Guid _atendimentoId = Guid.NewGuid();

    public string Status { get; set; } = "Bot";

    public bool Down { get; set; }

    public ConcurrentQueue<string> Calls { get; } = new();

    public List<(string Direction, string Sender, string Body, string? ExternalId)> Messages { get; } = [];

    public string? LastApiKey { get; private set; }

    public void Reset()
    {
        lock (_gate)
        {
            Status = "Bot";
            Down = false;
            Calls.Clear();
            Messages.Clear();
            _byExternalId.Clear();
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down)
        {
            throw new HttpRequestException("core fora do ar");
        }

        lock (_gate)
        {
            return Task.FromResult(Handle(request));
        }
    }

    private HttpResponseMessage Handle(HttpRequestMessage request)
    {
        LastApiKey = request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.Single() : null;
        if (LastApiKey != ValidKey)
        {
            Calls.Enqueue("unauthorized");
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();

        if (request.Method == HttpMethod.Post && path == "/api/v1/bot/atendimentos")
        {
            Calls.Enqueue("start");
            return Ok($$"""{"atendimentoId":"{{_atendimentoId}}","status":"{{Status}}","created":{{(Messages.Count == 0 ? "true" : "false")}}}""");
        }

        if (request.Method == HttpMethod.Get && path.EndsWith("/status", StringComparison.Ordinal))
        {
            Calls.Enqueue("status");
            return Ok($$"""{"atendimentoId":"{{_atendimentoId}}","status":"{{Status}}"}""");
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/handoff", StringComparison.Ordinal))
        {
            Calls.Enqueue("handoff");
            if (Status == "Bot")
            {
                Status = "PendingHuman";
            }

            return Ok("""{"success":true}""");
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/mensagens", StringComparison.Ordinal))
        {
            using var json = JsonDocument.Parse(body!);
            var root = json.RootElement;
            var direction = root.GetProperty("direction").GetString()!;
            var sender = root.GetProperty("sender").GetString()!;
            var text = root.GetProperty("body").GetString()!;
            var externalId = root.TryGetProperty("externalId", out var e) ? e.GetString() : null;
            Calls.Enqueue($"message:{direction}:{sender}");

            if (sender == "Bot" && Status == "Human")
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent("""{"errorCode":"ATENDIMENTO_BOT_BLOCKED"}""", Encoding.UTF8, "application/json"),
                };
            }

            // Idempotente por externalId, como o Core de verdade.
            if (externalId is not null && _byExternalId.TryGetValue(externalId, out var existing))
            {
                return Ok($$"""{"messageId":"{{existing}}"}""");
            }

            var id = Guid.NewGuid();
            if (externalId is not null)
            {
                _byExternalId[externalId] = id;
            }

            Messages.Add((direction, sender, text, externalId));
            return Ok($$"""{"messageId":"{{id}}"}""");
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>A Cloud API da Meta, do ponto de vista do Bot: registra os envios e pode falhar sob demanda.</summary>
public sealed class FakeMetaServer : HttpMessageHandler
{
    private int _counter;

    public ConcurrentQueue<(string PhoneNumberId, string To, string Body, string? Authorization)> Sent { get; } = new();

    public HttpStatusCode? FailWith { get; set; }

    public void Reset()
    {
        Sent.Clear();
        FailWith = null;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (FailWith is { } status)
        {
            FailWith = null;
            return new HttpResponseMessage(status);
        }

        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(body);
        var phoneNumberId = request.RequestUri!.AbsolutePath.Split('/')[2];

        Sent.Enqueue((
            phoneNumberId,
            json.RootElement.GetProperty("to").GetString()!,
            json.RootElement.GetProperty("text").GetProperty("body").GetString()!,
            request.Headers.Authorization?.ToString()));

        var wamid = $"wamid.OUT{Interlocked.Increment(ref _counter)}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"messages":[{"id":"{{wamid}}"}]}""", Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>Despacha pelo host de destino, para um único ponto de substituição dos handlers HTTP reais.</summary>
public sealed class HostRouterHandler(FakeCoreServer core, FakeMetaServer meta) : HttpMessageHandler
{
    private readonly HttpMessageInvoker _core = new(core, disposeHandler: false);
    private readonly HttpMessageInvoker _meta = new(meta, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = request.RequestUri!.Host switch
        {
            "core.test" => await _core.SendAsync(request, cancellationToken),
            "graph.test" => await _meta.SendAsync(request, cancellationToken),
            var other => throw new InvalidOperationException($"Host inesperado nos testes: {other}"),
        };

        // O HttpClient real sempre preenche RequestMessage; o Refit depende disso ao montar erros.
        response.RequestMessage ??= request;
        return response;
    }
}
