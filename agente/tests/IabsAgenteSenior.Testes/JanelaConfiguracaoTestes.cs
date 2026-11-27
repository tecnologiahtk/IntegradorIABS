using System.Reflection;
using IabsAgenteSenior.Configuracao;
using IabsAgenteSenior.Configurador;

namespace IabsAgenteSenior.Testes;

/// <summary>
/// Abre a janela de configuração de verdade (thread STA), com uma configuração
/// já gravada, e recarrega a configuração como o Salvar faz. Pegaria o erro de
/// 13/11/2026: a caixa do proxy sem opções quebrava ao recarregar.
/// Confere também que proxy e liberação do lançamento sobrevivem ao recarregar.
/// </summary>
public sealed class JanelaConfiguracaoTestes : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), $"iabs-janela-teste-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_pasta)) Directory.Delete(_pasta, recursive: true);
    }

    private static void EmThreadSta(Action acao)
    {
        Exception? erro = null;
        var t = new Thread(() =>
        {
            try
            {
                acao();
            }
            catch (Exception ex)
            {
                erro = ex;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (erro is not null) throw new Exception("A janela falhou.", erro);
    }

    [Fact]
    public void Abre_sem_configuracao()
    {
        var caminhos = Caminhos.Resolver(_pasta);
        EmThreadSta(() =>
        {
            using var janela = new JanelaPrincipal(caminhos) { ShowInTaskbar = false };
            janela.Show();
            Application.DoEvents();
            // sem configuração, a porta já vem com o padrão do SQL Server
            var porta = (TextBox)typeof(JanelaPrincipal).GetField("_porta", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(janela)!;
            Assert.Equal("1433", porta.Text);
            janela.Close();
        });
    }

    [Fact]
    public void Abre_e_recarrega_com_configuracao_gravada()
    {
        var caminhos = Caminhos.Resolver(_pasta);
        var cfg = ServicoConfiguracao.Montar(null, new EntradaConfiguracao
        {
            ApiUrl = "https://app.iabs.teste",
            ChaveApi = "chave-teste",
            Banco = new ConfiguracaoBanco { Servidor = "127.0.0.1", Porta = 14330, Banco = "SENIOR_TESTE", Usuario = "iabs_agente" },
            SenhaBanco = "senha-teste",
            Proxy = new ConfiguracaoProxy { Modo = "manual", Endereco = "http://proxy.teste.local:8080" },
            GravacaoLiberada = true,
        });
        new ConfiguracaoIntegrador { Conexoes = [cfg] }.Salvar(caminhos);

        EmThreadSta(() =>
        {
            using var janela = new JanelaPrincipal(caminhos) { ShowInTaskbar = false };
            janela.Show(); // OnLoad: carrega a configuração
            Application.DoEvents();
            // o Salvar recarrega a configuração depois de gravar
            typeof(JanelaPrincipal).GetMethod("CarregarConfiguracao", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(janela, [null]);
            // e a leitura dos campos preserva o proxy gravado, que não está na tela
            var entrada = (EntradaConfiguracao)typeof(JanelaPrincipal).GetMethod("LerCampos", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(janela, null)!;
            Assert.Equal("manual", entrada.Proxy!.Modo);
            Assert.Equal("http://proxy.teste.local:8080", entrada.Proxy.Endereco);
            Assert.Equal("iabs_agente", entrada.Banco.Usuario);
            // e a liberação do lançamento volta marcada
            Assert.True(entrada.GravacaoLiberada);
            Assert.Equal("Produção", entrada.Nome);
            // a única conexão não pode ser removida
            Assert.False(Campo<Button>(janela, "_removerConexao").Enabled);
            janela.Close();
        });
    }

    private static T Campo<T>(JanelaPrincipal janela, string nome) =>
        (T)typeof(JanelaPrincipal).GetField(nome, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(janela)!;

    /// <summary>2.2.0: o seletor no topo troca a conexão das abas 1 e 2.</summary>
    [Fact]
    public void Seletor_troca_a_conexao_das_abas()
    {
        var caminhos = Caminhos.Resolver(_pasta);
        ConfiguracaoLocal Conexao(string? nome, string url, string servidor, string chave) => ServicoConfiguracao.Montar(null, new EntradaConfiguracao
        {
            Nome = nome,
            ApiUrl = url,
            ChaveApi = chave,
            Banco = new ConfiguracaoBanco { Servidor = servidor, Banco = "SENIOR", Usuario = "iabs_agente" },
            SenhaBanco = "senha-teste",
        });
        new ConfiguracaoIntegrador
        {
            Conexoes = [Conexao(null, "https://app.iabs.teste", "srv-prod", "chave-prod"), Conexao("Homologação", "https://hml.iabs.teste", "srv-hml", "chave-hml")],
        }.Salvar(caminhos);

        EmThreadSta(() =>
        {
            using var janela = new JanelaPrincipal(caminhos) { ShowInTaskbar = false };
            janela.Show();
            Application.DoEvents();
            var seletor = Campo<ComboBox>(janela, "_conexao");
            Assert.Equal(["Produção", "Homologação"], seletor.Items.Cast<string>());
            Assert.Equal("srv-prod", Campo<TextBox>(janela, "_servidor").Text);

            // sem alteração na tela: troca sem perguntar
            seletor.SelectedItem = "Homologação";
            Assert.Equal("srv-hml", Campo<TextBox>(janela, "_servidor").Text);
            Assert.Equal("https://hml.iabs.teste", Campo<TextBox>(janela, "_url").Text);
            Assert.Equal("Homologação", Campo<TextBox>(janela, "_nome").Text);
            Assert.True(Campo<Button>(janela, "_removerConexao").Enabled);
            // a situação tem uma linha por conexão
            Assert.Equal(2, Campo<ListView>(janela, "_situacaoConexoes").Items.Count);
            janela.Close();
        });
    }
}
