using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using IabsAgenteSenior.Canal;
using IabsAgenteSenior.Configuracao;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IabsAgenteSenior.Testes;

/// <summary>
/// O canal de ponta a ponta contra uma "IABS" local (Kestrel com WebSocket):
/// handshake com a chave, ola/bemvindo, pedido e resposta, recusa da gravação,
/// fechamentos com código e o modo teste.
/// </summary>
public sealed class CanalTestes : IAsyncDisposable
{
    private const string Chave = "chave-do-integrador-teste";
    private WebApplication? _app;

    /// <summary>O que a "IABS" faz com a sessão aberta.</summary>
    private delegate Task Roteiro(WebSocket ws, HttpContext ctx);

    private async Task<string> SubirIabsAsync(Roteiro roteiro, int? recusarCom = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Map("/api/integrador/canal", async ctx =>
        {
            if (recusarCom is { } codigo || ctx.Request.Headers.Authorization != $"Bearer {Chave}")
            {
                ctx.Response.StatusCode = recusarCom ?? 401;
                return;
            }
            if (ctx.Request.Headers["X-Integrador-Protocolo"] != "1" || !ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { DangerousEnableCompression = true });
            await roteiro(ws, ctx);
        });
        await _app.StartAsync(TestContext.Current.CancellationToken);
        var endereco = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return endereco;
    }

    private static ConfiguracaoLocal Config(string endereco, bool gravacao = false) => new()
    {
        ApiUrl = endereco,
        ChaveApiProtegida = Protetor.Proteger(Chave),
        GravacaoLiberada = gravacao,
    };

    private static CanalIabs Canal(ConfiguracaoLocal cfg, OperacoesFalsas ops, EstadoCanal? estado = null) =>
        new(cfg, new Despachante(ops, () => cfg.GravacaoLiberada), ops.InfoAsync, estado ?? new EstadoCanal(), NullLogger.Instance);

    private static async Task<JsonDocument> ReceberAsync(WebSocket ws)
    {
        var buffer = new byte[1 << 20];
        var total = 0;
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), CancellationToken.None);
            total += r.Count;
        } while (!r.EndOfMessage);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, total));
    }

    private static Task EnviarAsync(WebSocket ws, object mensagem) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(mensagem), WebSocketMessageType.Text, true, CancellationToken.None);

    [Fact]
    public async Task Ola_pedido_resposta_e_fechamento_por_outra_sessao()
    {
        JsonElement? ola = null;
        JsonElement? leitura = null;
        JsonElement? gravacao = null;
        var endereco = await SubirIabsAsync(async (ws, _) =>
        {
            using (var d = await ReceberAsync(ws)) ola = d.RootElement.Clone();
            await EnviarAsync(ws, new { tipo = "bemvindo", conexao = "Cliente HML" });
            await EnviarAsync(ws, new { tipo = "pedido", id = "p1", op = "colaboradores", @params = new { empresas = new[] { 101 }, tipos = new[] { 1 }, setor_de = "LOCAL" } });
            using (var d = await ReceberAsync(ws)) leitura = d.RootElement.Clone();
            await EnviarAsync(ws, new { tipo = "pedido", id = "p2", op = "apagar", @params = new { } });
            using (var d = await ReceberAsync(ws)) gravacao = d.RootElement.Clone();
            await ws.CloseAsync((WebSocketCloseStatus)4002, "outra sessão", CancellationToken.None);
        });

        var ops = new OperacoesFalsas();
        var estado = new EstadoCanal();
        var cfg = Config(endereco);
        var fim = await new CanalIabs(cfg, new Despachante(ops, () => false), ops.InfoAsync, estado, NullLogger.Instance)
            .TestarAsyncSemTeste(TestContext.Current.CancellationToken);

        Assert.Equal("ola", ola!.Value.GetProperty("tipo").GetString());
        Assert.Equal(1, ola.Value.GetProperty("protocolo").GetInt32());
        Assert.Equal("SENIOR_TESTE", ola.Value.GetProperty("banco").GetProperty("nome").GetString());
        Assert.False(ola.Value.GetProperty("gravacao").GetBoolean());
        Assert.Contains("trocar", ola.Value.GetProperty("operacoes").EnumerateArray().Select(e => e.GetString()));

        Assert.Equal("p1", leitura!.Value.GetProperty("id").GetString());
        Assert.True(leitura.Value.GetProperty("ok").GetBoolean());
        var linha = leitura.Value.GetProperty("dados").GetProperty("linhas")[0];
        Assert.Equal(7, linha.GetProperty("numcad").GetInt32());
        Assert.Equal("2020-01-02", linha.GetProperty("datadm").GetString());
        Assert.Equal(1234.56m, linha.GetProperty("valsal").GetDecimal());

        Assert.False(gravacao!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal("GRAVACAO_DESLIGADA", gravacao.Value.GetProperty("erro").GetProperty("codigo").GetString());
        Assert.DoesNotContain("apagar", ops.Chamadas);

        Assert.Equal("SUBSTITUIDA", fim.Codigo);
        Assert.Equal(TimeSpan.FromMinutes(5), fim.EsperaLonga);
        Assert.Equal("Cliente HML", estado.Ler().ConexaoIabs);
        Assert.Equal(2, estado.Ler().Pedidos);
    }

    [Fact]
    public async Task Mensagem_fora_do_formato_e_ignorada_e_pedido_vencido_na_fila_nao_comeca()
    {
        JsonElement? demorado = null;
        JsonElement? vencido = null;
        JsonElement? depois = null;
        var endereco = await SubirIabsAsync(async (ws, _) =>
        {
            using (await ReceberAsync(ws)) { }
            await EnviarAsync(ws, new { tipo = "bemvindo", conexao = "c" });
            // fora do formato (2.1.4): não é objeto, "tipo" que não é texto, id que não é texto
            await ws.SendAsync("[1,2]"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
            await EnviarAsync(ws, new { tipo = 5 });
            await EnviarAsync(ws, new { tipo = "pedido", id = 7, op = "testar" });
            // o primeiro demora 300 ms; o segundo, com prazo de 100 ms, vence na fila
            await EnviarAsync(ws, new { tipo = "pedido", id = "p1", op = "testar", @params = new { }, prazo_ms = 5000 });
            await EnviarAsync(ws, new { tipo = "pedido", id = "p2", op = "testar", @params = new { }, prazo_ms = 100 });
            for (var i = 0; i < 2; i++)
            {
                using var d = await ReceberAsync(ws);
                var e = d.RootElement.Clone();
                if (e.GetProperty("id").GetString() == "p1") demorado = e;
                else vencido = e;
            }
            await EnviarAsync(ws, new { tipo = "pedido", id = "p3", op = "situacoes", @params = new { empresas = new[] { 101 }, tipos = new[] { 1 } } });
            using (var d = await ReceberAsync(ws)) depois = d.RootElement.Clone();
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "fim", CancellationToken.None);
        });

        var ops = new OperacoesFalsas { Demora = TimeSpan.FromMilliseconds(300) };
        var fim = await Canal(Config(endereco), ops).TestarAsyncSemTeste(TestContext.Current.CancellationToken);

        Assert.True(demorado!.Value.GetProperty("ok").GetBoolean());
        Assert.False(vencido!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal("PRAZO_VENCIDO", vencido.Value.GetProperty("erro").GetProperty("codigo").GetString());
        Assert.True(depois!.Value.GetProperty("ok").GetBoolean(), "o canal seguiu depois das mensagens fora do formato");
        Assert.Equal(1, ops.Chamadas.Count(c => c == "testar")); // o vencido não chegou ao banco
        Assert.Equal("FECHADA", fim.Codigo);
    }

    [Fact]
    public async Task Iabs_indisponivel_no_handshake_tenta_de_novo_sem_pedir_chave_nova()
    {
        var endereco = await SubirIabsAsync((_, _) => Task.CompletedTask, recusarCom: 503);
        var fim = await Canal(Config(endereco), new OperacoesFalsas()).TestarAsync(TestContext.Current.CancellationToken);
        Assert.Equal("IABS_INDISPONIVEL", fim.Codigo);
        Assert.Null(fim.EsperaLonga);
    }

    [Fact]
    public async Task Chave_recusada_no_handshake()
    {
        var endereco = await SubirIabsAsync((_, _) => Task.CompletedTask, recusarCom: 401);
        var fim = await Canal(Config(endereco), new OperacoesFalsas()).TestarAsync(TestContext.Current.CancellationToken);
        Assert.Equal("CHAVE_RECUSADA", fim.Codigo);
        Assert.False(fim.ChegouAConectar);
    }

    [Fact]
    public async Task Protocolo_antigo_no_handshake()
    {
        var endereco = await SubirIabsAsync((_, _) => Task.CompletedTask, recusarCom: 426);
        var fim = await Canal(Config(endereco), new OperacoesFalsas()).TestarAsync(TestContext.Current.CancellationToken);
        Assert.Equal("PROTOCOLO_ANTIGO", fim.Codigo);
        Assert.Equal(TimeSpan.FromMinutes(30), fim.EsperaLonga);
    }

    [Fact]
    public async Task Modo_teste_valida_e_fecha_com_o_cabecalho()
    {
        string? cabecalho = null;
        var endereco = await SubirIabsAsync(async (ws, ctx) =>
        {
            cabecalho = ctx.Request.Headers["X-Integrador-Teste"];
            using (await ReceberAsync(ws)) { }
            await EnviarAsync(ws, new { tipo = "bemvindo", conexao = "Cliente HML", teste = true });
            // o integrador fecha depois do bemvindo
            await ws.ReceiveAsync(new byte[64], CancellationToken.None);
        });
        var fim = await Canal(Config(endereco), new OperacoesFalsas()).TestarAsync(TestContext.Current.CancellationToken);
        Assert.Equal("OK", fim.Codigo);
        Assert.Equal("Cliente HML", fim.ConexaoIabs);
        Assert.Equal("1", cabecalho);
    }

    [Fact]
    public async Task Gravacao_liberada_chega_ao_banco()
    {
        JsonElement? resposta = null;
        var endereco = await SubirIabsAsync(async (ws, _) =>
        {
            using (await ReceberAsync(ws)) { }
            await EnviarAsync(ws, new { tipo = "bemvindo", conexao = "c" });
            await EnviarAsync(ws, new
            {
                tipo = "pedido", id = "g1", op = "lancar",
                @params = new { pessoa = new { numemp = 101, tipcol = 1, numcad = 4521 }, ini = "2026-11-20", fim = "2026-11-22", situacao = 14, atendente = 0, marca = "IABS HML #35474" },
            });
            using (var d = await ReceberAsync(ws)) resposta = d.RootElement.Clone();
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "fim", CancellationToken.None);
        });
        var ops = new OperacoesFalsas();
        var fim = await Canal(Config(endereco, gravacao: true), ops).TestarAsyncSemTeste(TestContext.Current.CancellationToken);
        Assert.True(resposta!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal(1, resposta.Value.GetProperty("dados").GetProperty("afetadas").GetInt32());
        Assert.Equal(new DateTime(2026, 11, 22), ops.UltimaNova!.Value.Fim);
        Assert.Equal("FECHADA", fim.Codigo);
    }

    [Fact]
    public async Task Servico_reconecta_depois_de_cair()
    {
        var sessoes = 0;
        var segunda = new TaskCompletionSource();
        var endereco = await SubirIabsAsync(async (ws, _) =>
        {
            var n = Interlocked.Increment(ref sessoes);
            using (await ReceberAsync(ws)) { }
            await EnviarAsync(ws, new { tipo = "bemvindo", conexao = "c" });
            if (n == 1)
            {
                ws.Abort(); // queda da rede
                return;
            }
            segunda.TrySetResult();
            await Task.Delay(Timeout.Infinite, TestContext.Current.CancellationToken).ContinueWith(_ => { });
        });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var estado = new EstadoCanal();
        var rodando = Canal(Config(endereco), new OperacoesFalsas(), estado).RodarAsync(cts.Token);
        await segunda.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.True(estado.Ler().Conectado);
        await cts.CancelAsync();
        await rodando.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(2, sessoes);
    }

    [Theory]
    [InlineData("https://app.iabs.med.br", "wss://app.iabs.med.br/api/integrador/canal")]
    [InlineData("https://app.iabs.med.br/qualquer/caminho", "wss://app.iabs.med.br/api/integrador/canal")]
    [InlineData("https://hml.iabs.med.br:8443/", "wss://hml.iabs.med.br:8443/api/integrador/canal")]
    [InlineData("http://127.0.0.1:5000", "ws://127.0.0.1:5000/api/integrador/canal")]
    public void Endereco_do_canal(string endereco, string esperado) =>
        Assert.Equal(esperado, Rede.UriDoCanal(endereco).ToString());

    [Theory]
    [InlineData("http://app.iabs.med.br")]
    [InlineData("ftp://app.iabs.med.br")]
    [InlineData("app.iabs.med.br")]
    public void Endereco_sem_https_e_recusado(string endereco) =>
        Assert.Throws<InvalidOperationException>(() => Rede.UriDoCanal(endereco));

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}

/// <summary>Uma sessão normal (sem o cabeçalho de teste), só para os testes.</summary>
internal static class CanalIabsTestes
{
    public static Task<FimDaSessao> TestarAsyncSemTeste(this CanalIabs canal, CancellationToken ct) => canal.SessaoUnicaAsync(ct);
}
