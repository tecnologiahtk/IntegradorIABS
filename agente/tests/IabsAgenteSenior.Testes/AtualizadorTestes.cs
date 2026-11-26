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

/// <summary>
/// O atualizador só instala a versão mais nova do repositório oficial, com o
/// SHA-256 da IABS igual ao do GitHub e ao do arquivo baixado.
/// </summary>
public sealed class AtualizadorTestes : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), $"iabs-atualizador-teste-{Guid.NewGuid():N}");
    private readonly Caminhos _caminhos;
    private static readonly byte[] Instalador = "MSI de teste — não é um instalador de verdade"u8.ToArray();
    private static readonly string ShaCerto = Convert.ToHexStringLower(SHA256.HashData(Instalador));
    private const string Versao = "9.9.9";

    public AtualizadorTestes() => _caminhos = Caminhos.Resolver(_pasta);

    public void Dispose()
    {
        if (Directory.Exists(_pasta)) Directory.Delete(_pasta, recursive: true);
    }

    /// <summary>Um GitHub falso: API da versão, página do download (302) e o armazenamento.</summary>
    private sealed class GitHubFalso(string shaNaApi, byte[] arquivo, string destinoDoRedirecionamento) : HttpMessageHandler
    {
        public List<string> Pedidos { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();
            Pedidos.Add(url);
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
        string shaDoPedido, string shaNaApi, byte[] arquivo, string destino = Armazenamento, string versao = Versao)
    {
        ArquivosAtualizacao.GravarPedido(_caminhos, new PedidoAtualizacao(versao, shaDoPedido, DateTimeOffset.Now));
        var github = new GitHubFalso(shaNaApi, arquivo, destino);
        var iniciados = new List<ProcessStartInfo>();
        var r = new Atualizador(_caminhos, NullLogger.Instance, github, p => { iniciados.Add(p); return true; })
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
        Assert.False(File.Exists(ArquivosAtualizacao.Pedido(_caminhos)), "o pedido é apagado: uma tentativa só");
        Assert.Equal("INSTALANDO", ArquivosAtualizacao.LerSituacao(_caminhos)!.Situacao);
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
        Assert.False(File.Exists(Path.Combine(ArquivosAtualizacao.Pasta(_caminhos), $"IntegradorIABS-{Versao}.msi")));
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

    [Fact]
    public void Sem_pedido_nao_faz_nada() =>
        Assert.Equal("SEM_PEDIDO", new Atualizador(_caminhos, NullLogger.Instance).ExecutarAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult().Situacao);

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
