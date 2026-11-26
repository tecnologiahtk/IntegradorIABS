using IabsAgenteSenior.Configuracao;

namespace IabsAgenteSenior.Testes;

public class ServicoConfiguracaoTestes
{
    private static EntradaConfiguracao Entrada(string chave = "chave-nova", string? senha = "senha-nova", string url = "https://app.iabs.teste") => new()
    {
        ApiUrl = url,
        ChaveApi = chave,
        Banco = new ConfiguracaoBanco { Servidor = "srv", Banco = "SENIOR", Usuario = "iabs" },
        SenhaBanco = senha,
    };

    [Fact]
    public void Monta_e_cifra_os_segredos()
    {
        var cfg = ServicoConfiguracao.Montar(null, Entrada());
        Assert.Equal("chave-nova", cfg.ChaveApi);
        Assert.Equal("senha-nova", cfg.Banco.Senha);
        Assert.DoesNotContain("senha-nova", cfg.Banco.SenhaProtegida);
        Assert.False(cfg.GravacaoLiberada);
    }

    [Fact]
    public void Segredo_em_branco_mantem_o_atual()
    {
        var atual = ServicoConfiguracao.Montar(null, Entrada());
        var nova = ServicoConfiguracao.Montar(atual, Entrada(chave: "", senha: ""));
        Assert.Equal("chave-nova", nova.ChaveApi);
        Assert.Equal("senha-nova", nova.Banco.Senha);
    }

    [Fact]
    public void Gravacao_liberada_vai_para_a_configuracao()
    {
        var e = Entrada();
        e.GravacaoLiberada = true;
        Assert.True(ServicoConfiguracao.Montar(null, e).GravacaoLiberada);
    }

    [Fact]
    public void Endereco_sem_https_e_recusado() =>
        Assert.Throws<InvalidOperationException>(() => ServicoConfiguracao.Montar(null, Entrada(url: "http://app.iabs.teste")));

    [Fact]
    public void Endereco_local_para_teste_e_aceito() =>
        ServicoConfiguracao.Montar(null, Entrada(url: "http://127.0.0.1:5000"));

    [Fact]
    public void Sem_nome_do_banco_e_recusado()
    {
        var e = Entrada();
        e.Banco.Banco = null;
        Assert.Throws<InvalidOperationException>(() => ServicoConfiguracao.Montar(null, e));
    }

    [Fact]
    public void Autenticacao_do_windows_dispensa_senha()
    {
        var e = Entrada(senha: null);
        e.Banco.AutenticacaoWindows = true;
        e.Banco.Usuario = null;
        var cfg = ServicoConfiguracao.Montar(null, e);
        Assert.Null(cfg.Banco.SenhaProtegida);
    }

    [Fact]
    public void Diagnostico_nao_leva_segredo()
    {
        var texto = System.Text.Json.JsonSerializer.Serialize(ServicoConfiguracao.Montar(null, Entrada()).SemSegredos());
        Assert.DoesNotContain("senha-nova", texto);
        Assert.DoesNotContain("chave-nova", texto);
    }
}
