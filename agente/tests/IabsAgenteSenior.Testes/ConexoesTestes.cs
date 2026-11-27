using System.Collections.Concurrent;
using System.Text.Json;
using IabsAgenteSenior.Canal;
using IabsAgenteSenior.Configuracao;
using IabsAgenteSenior.Servico;
using Microsoft.Extensions.Logging.Abstractions;

namespace IabsAgenteSenior.Testes;

/// <summary>
/// Várias conexões no mesmo integrador (2.2.0): ex.: Produção e Homologação,
/// cada uma com endereço da IABS, chave e banco do Senior próprios.
/// </summary>
public sealed class ConexoesTestes : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), $"iabs-conexoes-teste-{Guid.NewGuid():N}");
    private readonly Caminhos _caminhos;

    public ConexoesTestes() => _caminhos = Caminhos.Resolver(_pasta);

    public void Dispose()
    {
        if (Directory.Exists(_pasta)) Directory.Delete(_pasta, recursive: true);
    }

    private static EntradaConfiguracao Entrada(string? nome, string chave, string url = "https://app.iabs.teste", string banco = "SENIOR") => new()
    {
        Nome = nome,
        ApiUrl = url,
        ChaveApi = chave,
        Banco = new ConfiguracaoBanco { Servidor = "srv", Banco = banco, Usuario = "iabs" },
        SenhaBanco = "senha-teste",
    };

    private void GravarProducaoEHomologacao()
    {
        ServicoConfiguracao.Gravar(ServicoConfiguracao.Montar(null, Entrada(null, "chave-prod")), _caminhos, substitui: null);
        ServicoConfiguracao.Gravar(ServicoConfiguracao.Montar(null, Entrada("Homologação", "chave-hml", "https://hml.iabs.teste", "SENIOR_HML")), _caminhos, substitui: null);
    }

    // ---- arquivo ------------------------------------------------------------------------

    [Fact]
    public void Arquivo_da_2_1_vira_a_conexao_Producao()
    {
        Directory.CreateDirectory(_pasta);
        var legado = new
        {
            apiUrl = "https://app.iabs.teste",
            chaveApiProtegida = Protetor.Proteger("chave-antiga"),
            banco = new { servidor = "srv", banco = "SENIOR", usuario = "iabs" },
            gravacaoLiberada = true,
        };
        File.WriteAllText(_caminhos.ArquivoConfig, JsonSerializer.Serialize(legado));

        var c = Assert.Single(ConfiguracaoIntegrador.Carregar(_caminhos)!.Conexoes);
        Assert.Equal("Produção", c.Nome);
        Assert.Equal("https://app.iabs.teste", c.ApiUrl);
        Assert.Equal("chave-antiga", c.ChaveApi);
        Assert.Equal("SENIOR", c.Banco.Banco);
        Assert.True(c.GravacaoLiberada);
        Assert.Equal("sistema", c.Proxy.Modo);
    }

    [Fact]
    public void Segunda_conexao_entra_sem_mexer_na_primeira()
    {
        ServicoConfiguracao.Gravar(ServicoConfiguracao.Montar(null, Entrada(null, "chave-prod")), _caminhos, substitui: null);
        var primeira = ConfiguracaoIntegrador.Carregar(_caminhos)!.Conexoes[0];

        ServicoConfiguracao.Gravar(ServicoConfiguracao.Montar(null, Entrada("Homologação", "chave-hml", "https://hml.iabs.teste", "SENIOR_HML")), _caminhos, substitui: null);

        var arquivo = ConfiguracaoIntegrador.Carregar(_caminhos)!;
        Assert.Equal(["Produção", "Homologação"], arquivo.Conexoes.Select(c => c.Nome));
        Assert.Equal(JsonSerializer.Serialize(primeira, ConfiguracaoLocal.Json), JsonSerializer.Serialize(arquivo.Conexoes[0], ConfiguracaoLocal.Json));
        var hml = arquivo.Achar("homologação")!;
        Assert.Equal("chave-hml", hml.ChaveApi);
        Assert.Equal("https://hml.iabs.teste", hml.ApiUrl);
        Assert.Equal("SENIOR_HML", hml.Banco.Banco);
    }

    [Fact]
    public void Troca_de_nome_substitui_a_conexao_e_mantem_os_segredos()
    {
        GravarProducaoEHomologacao();
        var atual = ConfiguracaoIntegrador.Carregar(_caminhos)!.Achar("Homologação")!;
        var renomeada = ServicoConfiguracao.Montar(atual, Entrada("Homologação Senior", chave: "", "https://hml.iabs.teste", "SENIOR_HML"));
        ServicoConfiguracao.Gravar(renomeada, _caminhos, substitui: "Homologação");

        var arquivo = ConfiguracaoIntegrador.Carregar(_caminhos)!;
        Assert.Equal(["Produção", "Homologação Senior"], arquivo.Conexoes.Select(c => c.Nome));
        Assert.Equal("chave-hml", arquivo.Conexoes[1].ChaveApi);
    }

    [Fact]
    public void Nome_repetido_e_recusado()
    {
        GravarProducaoEHomologacao();
        var outra = ServicoConfiguracao.Montar(null, Entrada("PRODUÇÃO", "chave-outra"));
        var ex = Assert.Throws<InvalidOperationException>(() => ServicoConfiguracao.Gravar(outra, _caminhos, substitui: null));
        Assert.Contains("Já existe", ex.Message);
        Assert.Equal(2, ConfiguracaoIntegrador.Carregar(_caminhos)!.Conexoes.Count);
    }

    [Fact]
    public void Mesma_chave_em_duas_conexoes_e_recusada()
    {
        ServicoConfiguracao.Gravar(ServicoConfiguracao.Montar(null, Entrada(null, "chave-prod")), _caminhos, substitui: null);
        var hml = ServicoConfiguracao.Montar(null, Entrada("Homologação", "chave-prod", "https://hml.iabs.teste"));
        var ex = Assert.Throws<InvalidOperationException>(() => ServicoConfiguracao.Gravar(hml, _caminhos, substitui: null));
        Assert.Contains("já usa esta chave", ex.Message);
    }

    [Theory]
    [InlineData("Produção.2")]
    [InlineData("[Homologação]")]
    [InlineData(" ")]
    [InlineData("um nome comprido demais para uma conexão do integrador")]
    public void Nome_invalido_e_recusado(string nome) =>
        Assert.Throws<InvalidOperationException>(() => ServicoConfiguracao.NomeValido(nome));

    [Fact]
    public void Nome_com_acento_e_espacos_e_aceito() =>
        Assert.Equal("Homologação 2", ServicoConfiguracao.NomeValido("  Homologação   2 "));

    [Fact]
    public void Sem_nome_a_conexao_e_Producao() =>
        Assert.Equal("Produção", ServicoConfiguracao.Montar(null, Entrada(null, "chave")).Nome);

    [Fact]
    public void Remover_tira_so_a_conexao_pedida()
    {
        GravarProducaoEHomologacao();
        ServicoConfiguracao.Remover("homologação", _caminhos);
        Assert.Equal("Produção", Assert.Single(ConfiguracaoIntegrador.Carregar(_caminhos)!.Conexoes).Nome);
        Assert.Throws<InvalidOperationException>(() => ServicoConfiguracao.Remover("Outra", _caminhos));
    }

    [Fact]
    public void Nomes_vazios_ou_repetidos_no_arquivo_sao_completados()
    {
        Directory.CreateDirectory(_pasta);
        File.WriteAllText(_caminhos.ArquivoConfig,
            """{"conexoes":[{"apiUrl":"https://a.teste"},{"nome":"Produção","apiUrl":"https://b.teste"},{"nome":"produção","apiUrl":"https://c.teste"}]}""");
        var nomes = ConfiguracaoIntegrador.Carregar(_caminhos)!.Conexoes.Select(c => c.Nome);
        Assert.Equal(["Produção", "Produção 2", "produção 3"], nomes);
    }

    [Fact]
    public void Conexao_nova_herda_o_proxy_da_maquina()
    {
        var entrada = Entrada(null, "chave-prod");
        entrada.Proxy = new ConfiguracaoProxy { Modo = "manual", Endereco = "http://proxy.teste.local:8080", Usuario = "px" };
        entrada.SenhaProxy = "senha-proxy";
        ServicoConfiguracao.Gravar(ServicoConfiguracao.Montar(null, entrada), _caminhos, substitui: null);

        var arquivo = ConfiguracaoIntegrador.Carregar(_caminhos)!;
        var hml = ServicoConfiguracao.Montar(ServicoConfiguracao.BaseDaNova(arquivo), Entrada("Homologação", "chave-hml", "https://hml.iabs.teste"));
        Assert.Equal("manual", hml.Proxy.Modo);
        Assert.Equal("http://proxy.teste.local:8080", hml.Proxy.Endereco);
        Assert.Equal("senha-proxy", Protetor.Abrir(hml.Proxy.SenhaProtegida!));
        Assert.Equal("", ServicoConfiguracao.BaseDaNova(arquivo).ChaveApiProtegida);
    }

    [Fact]
    public void Diagnostico_lista_as_conexoes_sem_segredo()
    {
        GravarProducaoEHomologacao();
        var texto = JsonSerializer.Serialize(ConfiguracaoIntegrador.Carregar(_caminhos)!.SemSegredos());
        Assert.Contains("SENIOR_HML", texto);
        Assert.Contains("https://hml.iabs.teste", texto);
        Assert.DoesNotContain("chave-prod", texto);
        Assert.DoesNotContain("chave-hml", texto);
        Assert.DoesNotContain("senha-teste", texto);
    }

    // ---- canais em execução --------------------------------------------------------------

    private sealed class CanaisFalsos
    {
        public readonly ConcurrentQueue<string> Abertos = new();
        public readonly ConcurrentQueue<string> Fechados = new();

        public async Task Rodar(ConfiguracaoLocal cfg, EstadoCanal estado, CancellationToken ct)
        {
            Abertos.Enqueue(cfg.Nome);
            estado.Conectou(cfg.Nome);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                Fechados.Enqueue(cfg.Nome);
            }
        }
    }

    private static ConfiguracaoLocal Conexao(string nome, string url) => new() { Nome = nome, ApiUrl = url };

    [Fact]
    public async Task So_a_conexao_alterada_reabre()
    {
        var falsos = new CanaisFalsos();
        var execucao = new ConexoesEmExecucao(falsos.Rodar, NullLogger.Instance);
        await execucao.SincronizarAsync([Conexao("Produção", "https://app.iabs.teste"), Conexao("Homologação", "https://hml.iabs.teste")]);
        Assert.True(SpinWait.SpinUntil(() => falsos.Abertos.Count == 2, 5_000));

        // a homologação mudou: só ela fecha e abre de novo
        await execucao.SincronizarAsync([Conexao("Produção", "https://app.iabs.teste"), Conexao("Homologação", "https://hml2.iabs.teste")]);
        Assert.Equal(["Homologação"], falsos.Fechados);
        Assert.True(SpinWait.SpinUntil(() => falsos.Abertos.Count == 3, 5_000));

        // o mesmo conteúdo (lido de novo do arquivo): nada fecha
        await execucao.SincronizarAsync([Conexao("Produção", "https://app.iabs.teste"), Conexao("Homologação", "https://hml2.iabs.teste")]);
        Assert.Single(falsos.Fechados);

        // removida: fecha; a produção segue
        await execucao.SincronizarAsync([Conexao("Produção", "https://app.iabs.teste")]);
        Assert.Equal(["Homologação", "Homologação"], falsos.Fechados);
        var (cfg, estado) = Assert.Single(execucao.Retratos());
        Assert.Equal("Produção", cfg.Nome);
        Assert.True(estado.Conectado);

        await execucao.PararTodasAsync();
        Assert.Equal(["Homologação", "Homologação", "Produção"], falsos.Fechados);
        Assert.Empty(execucao.Retratos());
    }

    [Fact]
    public async Task Ordem_dos_retratos_segue_o_arquivo_sem_reabrir()
    {
        var falsos = new CanaisFalsos();
        var execucao = new ConexoesEmExecucao(falsos.Rodar, NullLogger.Instance);
        await execucao.SincronizarAsync([Conexao("Produção", "https://app.iabs.teste"), Conexao("Homologação", "https://hml.iabs.teste")]);
        await execucao.SincronizarAsync([Conexao("Homologação", "https://hml.iabs.teste"), Conexao("Produção", "https://app.iabs.teste")]);
        Assert.Equal(["Homologação", "Produção"], execucao.Retratos().Select(r => r.Cfg.Nome));
        Assert.Empty(falsos.Fechados);
        await execucao.PararTodasAsync();
    }
}
