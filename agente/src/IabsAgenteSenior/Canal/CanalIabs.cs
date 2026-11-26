using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using IabsAgenteSenior.Banco;
using IabsAgenteSenior.Configuracao;
using Microsoft.Extensions.Logging;

namespace IabsAgenteSenior.Canal;

/// <summary>Como uma sessão do canal terminou.</summary>
public sealed record FimDaSessao(string Codigo, string Mensagem, bool ChegouAConectar, string? ConexaoIabs = null)
{
    /// <summary>Recusa que não melhora tentando logo de novo (chave, protocolo, outra instalação).</summary>
    public TimeSpan? EsperaLonga => Codigo switch
    {
        "CHAVE_RECUSADA" or "CONEXAO_EXCLUIDA" or "CHAVE_TROCADA" or "SUBSTITUIDA" => TimeSpan.FromMinutes(5),
        "PROTOCOLO_ANTIGO" => TimeSpan.FromMinutes(30),
        _ => null,
    };
}

/// <summary>
/// O canal com a IABS (docs/protocolo-canal.md): uma conexão WebSocket de
/// saída, mantida aberta, por onde a IABS pede as operações fixas e o
/// integrador responde. Reconecta sozinho. Um pedido por vez.
/// </summary>
public sealed class CanalIabs(
    ConfiguracaoLocal cfg,
    Despachante despachante,
    Func<CancellationToken, Task<InfoBanco>> infoBanco,
    EstadoCanal estado,
    ILogger logger,
    Func<HttpMessageInvoker>? criarInvoker = null)
{
    private static readonly TimeSpan[] Esperas =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
         TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    /// <summary>Conectado por mais que isso: a próxima queda volta a esperar 1 s.</summary>
    private static readonly TimeSpan ConexaoEstavel = TimeSpan.FromSeconds(60);

    private const int TamanhoMaximoDoPedido = 1024 * 1024;
    private static readonly TimeSpan TempoMaximoDaOperacao = TimeSpan.FromMinutes(5);

    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };

    /// <summary>Intervalo do ping do WebSocket (os dois lados pingam).</summary>
    public TimeSpan IntervaloPing { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Sem pong nesse tempo, a conexão é dada como caída.</summary>
    public TimeSpan TempoSemPong { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Fica conectando e atendendo até o serviço parar.</summary>
    public async Task RodarAsync(CancellationToken ct)
    {
        var tentativa = 0;
        while (!ct.IsCancellationRequested)
        {
            var inicio = DateTimeOffset.Now;
            FimDaSessao fim;
            try
            {
                fim = await SessaoAsync(teste: false, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            estado.Desconectou(fim.Mensagem);

            if (fim.ChegouAConectar && DateTimeOffset.Now - inicio >= ConexaoEstavel) tentativa = 0;
            var espera = fim.EsperaLonga ?? ComVariacao(Esperas[Math.Min(tentativa++, Esperas.Length - 1)]);
            logger.Log(fim.EsperaLonga is null ? LogLevel.Information : LogLevel.Warning,
                "Canal com a IABS fechado ({Codigo}): {Mensagem} Nova tentativa em {Espera:0} s.", fim.Codigo, fim.Mensagem, espera.TotalSeconds);
            try
            {
                await Task.Delay(espera, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Espera com ±20 %: vários integradores não voltam todos no mesmo segundo.</summary>
    private static TimeSpan ComVariacao(TimeSpan t) => t * (0.8 + Random.Shared.NextDouble() * 0.4);

    /// <summary>"Testar conexão" da janela: valida a chave e fecha, sem derrubar a sessão do serviço.</summary>
    public Task<FimDaSessao> TestarAsync(CancellationToken ct) => SessaoAsync(teste: true, ct);

    /// <summary>Uma sessão normal, sem reconectar (testes).</summary>
    internal Task<FimDaSessao> SessaoUnicaAsync(CancellationToken ct) => SessaoAsync(teste: false, ct);

    private async Task<FimDaSessao> SessaoAsync(bool teste, CancellationToken ct)
    {
        Uri uri;
        string chave;
        try
        {
            uri = Rede.UriDoCanal(cfg.ApiUrl);
            chave = cfg.ChaveApi;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            return new FimDaSessao("CONFIGURACAO", ex is InvalidOperationException ? ex.Message : "Chave do integrador ilegível: salve a configuração de novo.", false);
        }

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {chave}");
        ws.Options.SetRequestHeader("X-Integrador-Versao", Constantes.Versao);
        ws.Options.SetRequestHeader("X-Integrador-Protocolo", Constantes.Protocolo.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (teste) ws.Options.SetRequestHeader("X-Integrador-Teste", "1");
        ws.Options.KeepAliveInterval = IntervaloPing;
        ws.Options.KeepAliveTimeout = TempoSemPong;
        ws.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
        ws.Options.CollectHttpResponseDetails = true;

        using var invoker = criarInvoker?.Invoke() ?? new HttpMessageInvoker(Rede.CriarHandler(cfg.Proxy));
        using (var limite = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            limite.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await ws.ConnectAsync(uri, invoker, limite.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new FimDaSessao("SEM_RESPOSTA", "A IABS não respondeu em 30 segundos (rede, proxy ou firewall).", false);
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
            {
                return (int)ws.HttpStatusCode switch
                {
                    401 => new FimDaSessao("CHAVE_RECUSADA", "A IABS recusou a chave do integrador. Gere uma chave nova na tela de Integrações da IABS e salve aqui.", false),
                    403 => new FimDaSessao("CONEXAO_EXCLUIDA", "A conexão desta chave foi excluída na IABS.", false),
                    426 => new FimDaSessao("PROTOCOLO_ANTIGO", "Esta versão do integrador não é mais aceita pela IABS: instale a versão nova.", false),
                    0 => new FimDaSessao("SEM_RESPOSTA", $"Não consegui chegar à IABS ({uri.Host}): confira a rede, o proxy e o firewall.", false),
                    var codigo => new FimDaSessao("SEM_RESPOSTA", $"A IABS respondeu {codigo} ao abrir o canal.", false),
                };
            }
        }

        logger.LogInformation("Canal com a IABS aberto ({Host}).", uri.Host);
        using var sessao = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var envio = new SemaphoreSlim(1, 1);
        var fila = new SemaphoreSlim(1, 1);
        var pendentes = new List<Task>();
        string? conexaoIabs = null;
        try
        {
            await EnviarAsync(ws, envio, await OlaAsync(sessao.Token), sessao.Token);
            while (ws.State == WebSocketState.Open)
            {
                var texto = await ReceberAsync(ws, sessao.Token);
                if (texto is null)
                {
                    await FecharAsync(ws, "fechado pela IABS"); // completa o fechamento
                    break;
                }
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(texto);
                }
                catch (JsonException)
                {
                    logger.LogWarning("Mensagem da IABS ilegível; ignorada.");
                    continue;
                }
                using (doc)
                {
                    var raiz = doc.RootElement;
                    var tipo = raiz.TryGetProperty("tipo", out var t) ? t.GetString() : null;
                    if (tipo == "bemvindo")
                    {
                        conexaoIabs = raiz.TryGetProperty("conexao", out var c) ? c.GetString() : null;
                        estado.Conectou(conexaoIabs);
                        if (teste)
                        {
                            await FecharAsync(ws, "teste concluído");
                            return new FimDaSessao("OK", "Conexão com a IABS funcionando.", true, conexaoIabs);
                        }
                        logger.LogInformation("Conectado à IABS (conexão \"{Conexao}\").", conexaoIabs);
                    }
                    else if (tipo == "pedido")
                    {
                        var id = raiz.TryGetProperty("id", out var i) ? i.GetString() : null;
                        var op = raiz.TryGetProperty("op", out var o) ? o.GetString() : null;
                        var parametros = raiz.TryGetProperty("params", out var p) ? p.Clone() : default;
                        if (id is null || op is null) continue;
                        pendentes.RemoveAll(x => x.IsCompleted);
                        pendentes.Add(AtenderAsync(ws, envio, fila, id, op, parametros, sessao.Token));
                    }
                }
            }
            return FimPeloFechamento(ws, conexaoIabs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FecharAsync(ws, "serviço parando");
            throw;
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
        {
            return new FimDaSessao("CAIU", "A conexão com a IABS caiu.", true, conexaoIabs);
        }
        finally
        {
            await sessao.CancelAsync();
            try
            {
                await Task.WhenAll(pendentes).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (Exception)
            {
                // pedido em andamento cancelado com a sessão: a IABS repete
            }
        }
    }

    private static FimDaSessao FimPeloFechamento(ClientWebSocket ws, string? conexao) => (int?)ws.CloseStatus switch
    {
        4001 => new FimDaSessao("CHAVE_TROCADA", "A chave do integrador foi trocada na IABS: salve a chave nova aqui.", true, conexao),
        4002 => new FimDaSessao("SUBSTITUIDA", "Outro integrador com a mesma chave se conectou à IABS. Deixe só uma instalação por conexão.", true, conexao),
        4003 => new FimDaSessao("CONEXAO_EXCLUIDA", "A conexão desta chave foi excluída na IABS.", true, conexao),
        _ => new FimDaSessao("FECHADA", "A IABS fechou a conexão.", true, conexao),
    };

    private async Task<object> OlaAsync(CancellationToken ct)
    {
        object? banco = null;
        string? erro = null;
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var info = await infoBanco(limite.Token);
            banco = new { produto = info.Produto, versao = info.Versao, nome = info.Nome };
        }
        catch (ErroOperacao e)
        {
            erro = e.Codigo;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            erro = "BANCO_SEM_RESPOSTA";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // configuração do banco incompleta ou inválida: o canal sobe mesmo assim
            erro = "INTEGRADOR_FALHOU";
        }
        if (erro is not null) logger.LogWarning("Banco do Senior indisponível ao conectar à IABS ({Codigo}).", erro);
        return new
        {
            tipo = "ola",
            versao = Constantes.Versao,
            protocolo = Constantes.Protocolo,
            maquina = Environment.MachineName,
            banco,
            banco_erro = erro,
            gravacao = cfg.GravacaoLiberada,
            operacoes = Despachante.Nomes,
        };
    }

    private async Task AtenderAsync(ClientWebSocket ws, SemaphoreSlim envio, SemaphoreSlim fila, string id, string op, JsonElement parametros, CancellationToken ct)
    {
        await fila.WaitAsync(ct);
        object resposta;
        var relogio = Stopwatch.StartNew();
        try
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(TempoMaximoDaOperacao);
            var dados = await despachante.ExecutarAsync(op, parametros, limite.Token);
            resposta = new { tipo = "resposta", id, ok = true, dados };
            logger.LogInformation("Pedido {Op}: ok em {Ms} ms{Linhas}.", op, relogio.ElapsedMilliseconds, Contagem(dados));
        }
        catch (ErroOperacao e)
        {
            resposta = new { tipo = "resposta", id, ok = false, erro = new { codigo = e.Codigo, detalhe = e.Detalhe } };
            logger.LogWarning("Pedido {Op}: {Codigo} em {Ms} ms.", op, e.Codigo, relogio.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // a sessão acabou: a IABS dá o pedido por perdido e repete
        }
        catch (OperationCanceledException)
        {
            resposta = new { tipo = "resposta", id, ok = false, erro = new { codigo = "BANCO_SEM_RESPOSTA", detalhe = new { } } };
            logger.LogWarning("Pedido {Op}: tempo esgotado em {Ms} ms.", op, relogio.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // o texto da exceção pode ter dado do banco: no log, só o tipo
            resposta = new { tipo = "resposta", id, ok = false, erro = new { codigo = "INTEGRADOR_FALHOU", detalhe = new { tipo = ex.GetType().Name } } };
            logger.LogError("Pedido {Op}: erro inesperado ({Tipo}).", op, ex.GetType().Name);
        }
        finally
        {
            fila.Release();
        }
        estado.Atendeu();
        try
        {
            await EnviarAsync(ws, envio, resposta, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // canal caiu antes da resposta: a IABS repete o pedido
        }
    }

    private static string Contagem(object dados)
    {
        var linhas = dados.GetType().GetProperty("linhas")?.GetValue(dados) as System.Collections.ICollection;
        return linhas is null ? "" : $" ({linhas.Count} linhas)";
    }

    private static async Task EnviarAsync(ClientWebSocket ws, SemaphoreSlim envio, object mensagem, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(mensagem, Json);
        await envio.WaitAsync(ct);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            envio.Release();
        }
    }

    /// <summary>Uma mensagem de texto inteira; nulo quando a IABS fecha.</summary>
    private static async Task<string?> ReceberAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var corpo = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            corpo.Write(buffer, 0, r.Count);
            if (corpo.Length > TamanhoMaximoDoPedido) throw new WebSocketException("Mensagem da IABS maior que o limite.");
            if (r.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(corpo.GetBuffer(), 0, (int)corpo.Length);
    }

    private static async Task FecharAsync(ClientWebSocket ws, string motivo)
    {
        if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, motivo, limite.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // fecha de qualquer jeito
        }
    }
}
