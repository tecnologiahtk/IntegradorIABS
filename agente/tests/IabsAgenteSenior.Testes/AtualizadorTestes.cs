using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using IabsAgenteSenior.Atualizacao;
using IabsAgenteSenior.Banco;
using IabsAgenteSenior.Canal;
using IabsAgenteSenior.Configuracao;
using Microsoft.Extensions.Logging.Abstractions;

namespace IabsAgenteSenior.Testes;

/// <summary>A guarda do atualizador em memória (no serviço, o registro).</summary>
internal sealed class GuardaEmMemoria : IGuardaDaAtualizacao
{
    public SituacaoAtualizacao? Situacao { get; private set; }
    private DateTimeOffset? _pedido;

    public SituacaoAtualizacao? Ler() => Situacao;
    public void Gravar(SituacaoAtualizacao s) => Situacao = s;
    public DateTimeOffset? PedidoTratado() => _pedido;
    public void MarcarPedidoTratado(DateTimeOffset em) => _pedido = em;
}

/// <summary>
/// O atualizador só instala a versão mais nova do repositório oficial, com o
/// SHA-256 da IABS igual ao do GitHub e ao do arquivo baixado. Ele roda como
/// SYSTEM e não grava nem apaga nada na pasta de dados (revisão de 27/11/2026).
/// </summary>
public sealed class AtualizadorTestes : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), $"iabs-atualizador-teste-{Guid.NewGuid():N}");
    private readonly string _pastaDoSistema = Path.Combine(Path.GetTempPath(), $"iabs-atualizador-sistema-{Guid.NewGuid():N}");
    private readonly Caminhos _caminhos;
    private readonly GuardaEmMemoria _guarda = new();
    private readonly AreaDoSistema _area;
    private static readonly byte[] Instalador = "MSI de teste — não é um instalador de verdade"u8.ToArray();
    private static readonly string ShaCerto = Convert.ToHexStringLower(SHA256.HashData(Instalador));
    private const string Versao = "9.9.9";

    public AtualizadorTestes()
    {
        _caminhos = Caminhos.Resolver(_pasta);
        _area = new AreaDoSistema(_pastaDoSistema, _guarda);
    }

    public void Dispose()
    {
        if (Directory.Exists(_pasta)) Directory.Delete(_pasta, recursive: true);
        if (Directory.Exists(_pastaDoSistema)) Directory.Delete(_pastaDoSistema, recursive: true);
    }

    private static readonly string ApiDaUltima = $"https://api.github.com/repos/{Constantes.RepositorioOficial}/releases/latest";

    /// <summary>
    /// Um GitHub falso: API da última versão (<paramref name="ultima"/>; nula = fora do ar),
    /// API da versão, página do download (302) e o armazenamento.
    /// </summary>
    private sealed class GitHubFalso(string shaNaApi, byte[] arquivo, string destinoDoRedirecionamento, string? ultima = null) : HttpMessageHandler
    {
        public List<string> Pedidos { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();
            Pedidos.Add(url);
            if (url == ApiDaUltima)
            {
                if (ultima is null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                var ultimaCorpo = JsonSerializer.Serialize(new
                {
                    tag_name = $"v{ultima}",
                    assets = new[]
                    {
                        new { name = "IntegradorIABS.msi", digest = $"sha256:{shaNaApi}", browser_download_url = RegrasAtualizacao.UrlDoInstalador(ultima).ToString() },
                    },
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ultimaCorpo) });
            }
            if (url == RegrasAtualizacao.ApiDaVersao(Versao).ToString())
            {
                var corpo = JsonSerializer.Serialize(new
                {
                    tag_name = $"v{Versao}",
                    assets = new[]
                    {
                        new { name = "IntegradorIABS.msi", digest = $"sha256:{shaNaApi}", browser_download_url = RegrasAtualizacao.UrlDoInstalador(Versao).ToString() },
                    },
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(corpo) });
            }
            if (url == RegrasAtualizacao.UrlDoInstalador(Versao).ToString())
            {
                var redir = new HttpResponseMessage(HttpStatusCode.Found);
                redir.Headers.Location = new Uri(destinoDoRedirecionamento);
                return Task.FromResult(redir);
            }
            if (url == destinoDoRedirecionamento)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(arquivo) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private const string Armazenamento = "https://release-assets.githubusercontent.com/github-production-release-asset/1/arquivo";

    private (SituacaoAtualizacao R, List<ProcessStartInfo> Iniciados, GitHubFalso GitHub) Rodar(
        string shaDoPedido, string shaNaApi, byte[] arquivo, string destino = Armazenamento, string versao = Versao, bool novoPedido = true, string? ultima = null)
    {
        if (novoPedido) ArquivosAtualizacao.GravarPedido(_caminhos, new PedidoAtualizacao(versao, shaDoPedido, DateTimeOffset.Now));
        var github = new GitHubFalso(shaNaApi, arquivo, destino, ultima);
        var iniciados = new List<ProcessStartInfo>();
        var r = new Atualizador(_caminhos, NullLogger.Instance, github, p => { iniciados.Add(p); return true; }, _area)
            .ExecutarAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        return (r, iniciados, github);
    }

    [Fact]
    public void Confere_os_tres_sha256_e_roda_o_msiexec_sem_janela()
    {
        var (r, iniciados, github) = Rodar(ShaCerto, ShaCerto, Instalador);
        Assert.Equal("INSTALANDO", r.Situacao);
        var p = Assert.Single(iniciados);
        Assert.EndsWith("msiexec.exe", p.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/i", p.ArgumentList[0]);
        Assert.EndsWith($"IntegradorIABS-{Versao}.msi", p.ArgumentList[1]);
        Assert.Contains("/qn", p.ArgumentList);
        Assert.Equal(Instalador, File.ReadAllBytes(p.ArgumentList[1]));
        Assert.Contains(Armazenamento, github.Pedidos);
        Assert.Equal("INSTALANDO", ArquivosAtualizacao.LerSituacao(_caminhos, _guarda)!.Situacao);
    }

    [Fact]
    public void Instalador_e_log_ficam_na_area_do_sistema_e_nada_e_gravado_na_pasta_de_dados()
    {
        var (_, iniciados, _) = Rodar(ShaCerto, ShaCerto, Instalador);
        var p = Assert.Single(iniciados);
        var msi = p.ArgumentList[1];
        var log = p.ArgumentList[p.ArgumentList.IndexOf("/l*v") + 1];
        Assert.StartsWith(_area.PastaInstaladores + Path.DirectorySeparatorChar, msi);
        Assert.Equal(Path.GetDirectoryName(msi), Path.GetDirectoryName(log));
        // a pasta de dados só tem o pedido, que o atualizador leu e não apagou
        Assert.Equal([ArquivosAtualizacao.Pedido(_caminhos)], Directory.GetFileSystemEntries(_pasta));
        Assert.Equal("INSTALANDO", _guarda.Situacao!.Situacao);
    }

    [Fact]
    public void Pedido_ja_tratado_nao_roda_de_novo()
    {
        Rodar(ShaCerto, ShaCerto, Instalador);
        // ligado de novo sem pedido novo: não repete o pedido; só olha a última versão (aqui, a instalada)
        var (r, iniciados, github) = Rodar(ShaCerto, ShaCerto, Instalador, novoPedido: false, ultima: Constantes.Versao);
        Assert.Equal("JA_E_A_MAIS_NOVA", r.Codigo);
        Assert.Empty(iniciados);
        Assert.Equal([ApiDaUltima], github.Pedidos);
        // pedido novo (outro PedidoEm) roda
        var (r2, iniciados2, _) = Rodar(ShaCerto, ShaCerto, Instalador);
        Assert.Equal("INSTALANDO", r2.Situacao);
        Assert.Single(iniciados2);
    }

    [Fact]
    public void Situacao_mais_recente_entre_o_pedido_e_o_atualizador()
    {
        var antes = DateTimeOffset.Now.AddMinutes(-5);
        ArquivosAtualizacao.GravarSituacao(_caminhos, new SituacaoAtualizacao(Versao, "PEDIDA", null, antes));
        Assert.Equal("PEDIDA", ArquivosAtualizacao.LerSituacao(_caminhos, _guarda)!.Situacao);
        _guarda.Gravar(new SituacaoAtualizacao(Versao, "INSTALANDO", null, antes.AddMinutes(1)));
        Assert.Equal("INSTALANDO", ArquivosAtualizacao.LerSituacao(_caminhos, _guarda)!.Situacao);
        // pedido novo depois da última atualização
        ArquivosAtualizacao.GravarSituacao(_caminhos, new SituacaoAtualizacao(Versao, "PEDIDA", null, antes.AddMinutes(2)));
        Assert.Equal("PEDIDA", ArquivosAtualizacao.LerSituacao(_caminhos, _guarda)!.Situacao);
    }

    [Fact]
    public void Sha256_da_iabs_diferente_do_github_nao_baixa_nem_instala()
    {
        var (r, iniciados, github) = Rodar(ShaCerto, new string('a', 64), Instalador);
        Assert.Equal("SHA256_DIVERGENTE_GITHUB", r.Codigo);
        Assert.Empty(iniciados);
        Assert.DoesNotContain(Armazenamento, github.Pedidos);
    }

    [Fact]
    public void Arquivo_adulterado_e_apagado_e_nao_instala()
    {
        var (r, iniciados, _) = Rodar(ShaCerto, ShaCerto, "outro conteúdo"u8.ToArray());
        Assert.Equal("SHA256_DIVERGENTE_ARQUIVO", r.Codigo);
        Assert.Empty(iniciados);
        Assert.Empty(Directory.GetFiles(_area.PastaInstaladores, "*.msi", SearchOption.AllDirectories));
    }

    [Fact]
    public void Redirecionamento_para_fora_do_github_e_recusado()
    {
        var (r, iniciados, _) = Rodar(ShaCerto, ShaCerto, Instalador, destino: "https://instalador.malicioso.example/arquivo");
        Assert.Equal("ENDERECO_NAO_PERMITIDO", r.Codigo);
        Assert.Empty(iniciados);
    }

    [Theory]
    [InlineData("0.0.1")]
    [InlineData("2.1.0")]
    public void Versao_que_nao_e_mais_nova_nao_vai(string versao)
    {
        var (r, iniciados, github) = Rodar(ShaCerto, ShaCerto, Instalador, versao: versao);
        Assert.Equal("VERSAO_NAO_E_MAIS_NOVA", r.Codigo);
        Assert.Empty(iniciados);
        Assert.Empty(github.Pedidos);
    }

    /// <summary>A API "latest" falsa do GitHub, para o "Verificar atualização" da janela.</summary>
    private sealed class UltimaFalsa(string tag, string? url) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Assert.Equal($"https://api.github.com/repos/{Constantes.RepositorioOficial}/releases/latest", r.RequestUri!.ToString());
            var corpo = JsonSerializer.Serialize(new
            {
                tag_name = tag,
                assets = new[] { new { name = "IntegradorIABS.msi", digest = $"sha256:{ShaCerto}", browser_download_url = url } },
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(corpo) });
        }
    }

    [Fact]
    public async Task Verificar_atualizacao_le_a_ultima_versao_publicada()
    {
        var v = await VerificadorAtualizacao.UltimaVersaoAsync(new ConfiguracaoProxy(), TestContext.Current.CancellationToken,
            new UltimaFalsa("v9.9.9", RegrasAtualizacao.UrlDoInstalador("9.9.9").ToString()));
        Assert.Equal(new VersaoPublicada("9.9.9", ShaCerto), v);
        // instalador fora do lugar oficial ou versão sem formato: não oferece
        Assert.Null(await VerificadorAtualizacao.UltimaVersaoAsync(new ConfiguracaoProxy(), TestContext.Current.CancellationToken,
            new UltimaFalsa("v9.9.9", "https://example.com/IntegradorIABS.msi")));
        Assert.Null(await VerificadorAtualizacao.UltimaVersaoAsync(new ConfiguracaoProxy(), TestContext.Current.CancellationToken,
            new UltimaFalsa("ultima", RegrasAtualizacao.UrlDoInstalador("9.9.9").ToString())));
    }

    // ---- ligado sem pedido: o botão Atualizar da janela, por qualquer usuário (2.2.1) ----

    [Fact]
    public void Ligado_sem_pedido_instala_a_ultima_versao_do_github()
    {
        var (r, iniciados, github) = Rodar(ShaCerto, ShaCerto, Instalador, novoPedido: false, ultima: Versao);
        Assert.Equal("INSTALANDO", r.Situacao);
        Assert.Single(iniciados);
        Assert.Equal(ApiDaUltima, github.Pedidos[0]);
        Assert.Contains(RegrasAtualizacao.ApiDaVersao(Versao).ToString(), github.Pedidos);
        // ninguém gravou nada na pasta de dados
        Assert.True(!Directory.Exists(_pasta) || !Directory.EnumerateFileSystemEntries(_pasta).Any());
    }

    [Fact]
    public void Ligado_sem_pedido_ja_na_mais_nova_nao_instala_nem_registra()
    {
        var (r, iniciados, _) = Rodar(ShaCerto, ShaCerto, Instalador, novoPedido: false, ultima: Constantes.Versao);
        Assert.Equal("SEM_PEDIDO", r.Situacao);
        Assert.Equal("JA_E_A_MAIS_NOVA", r.Codigo);
        Assert.Empty(iniciados);
        Assert.Null(_guarda.Situacao);
    }

    [Fact]
    public void Ligado_sem_pedido_com_o_github_fora_registra_o_erro()
    {
        var (r, iniciados, _) = Rodar(ShaCerto, ShaCerto, Instalador, novoPedido: false, ultima: null);
        Assert.Equal("GITHUB_INDISPONIVEL", r.Codigo);
        Assert.Empty(iniciados);
        Assert.Equal("ERRO", _guarda.Situacao!.Situacao);
    }

    [Fact]
    public void Ligado_sem_pedido_com_arquivo_adulterado_nao_instala()
    {
        var (r, iniciados, _) = Rodar(ShaCerto, ShaCerto, "outro conteúdo"u8.ToArray(), novoPedido: false, ultima: Versao);
        Assert.Equal("SHA256_DIVERGENTE_ARQUIVO", r.Codigo);
        Assert.Empty(iniciados);
    }

    [Theory]
    [InlineData("https://github.com/x", true)]
    [InlineData("https://api.github.com/x", true)]
    [InlineData("https://release-assets.githubusercontent.com/x", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("http://github.com/x", false)]
    [InlineData("https://github.com.malicioso.example/x", false)]
    [InlineData("https://githubusercontent.com.example/x", false)]
    public void So_github_e_https(string url, bool permitido) =>
        Assert.Equal(permitido, RegrasAtualizacao.HostPermitido(new Uri(url)));

    [Fact]
    public void Pedido_pelo_canal_valida_e_deixa_para_o_atualizador()
    {
        PedidoAtualizacao? recebido = null;
        var d = new Despachante(new OperacoesFalsas(), () => false, (p, _) => { recebido = p; return Task.CompletedTask; });
        var ct = TestContext.Current.CancellationToken;
        var ok = d.ExecutarAsync("atualizar_integrador", JsonDocument.Parse($$"""{"versao":"9.9.9","sha256":"{{ShaCerto}}"}""").RootElement, ct).GetAwaiter().GetResult();
        Assert.Contains("AGENDADA", JsonSerializer.Serialize(ok));
        Assert.Equal("9.9.9", recebido!.Versao);

        var antiga = Assert.ThrowsAsync<ErroOperacao>(() => d.ExecutarAsync("atualizar_integrador", JsonDocument.Parse($$"""{"versao":"1.0.0","sha256":"{{ShaCerto}}"}""").RootElement, ct)).GetAwaiter().GetResult();
        Assert.Equal("ATUALIZACAO_DESNECESSARIA", antiga.Codigo);
        var semSha = Assert.ThrowsAsync<ErroOperacao>(() => d.ExecutarAsync("atualizar_integrador", JsonDocument.Parse("""{"versao":"9.9.9","sha256":"abc"}""").RootElement, ct)).GetAwaiter().GetResult();
        Assert.Equal("sha256", semSha.Detalhe["campo"]);
    }
}
