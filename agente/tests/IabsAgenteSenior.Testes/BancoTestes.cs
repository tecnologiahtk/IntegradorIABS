using IabsAgenteSenior.Banco;
using IabsAgenteSenior.Configuracao;
using Microsoft.Extensions.Logging.Abstractions;

namespace IabsAgenteSenior.Testes;

public class BancoTestes
{
    [Theory]
    [InlineData(2627, false, "CLIENTE_CHAVE_REPETIDA")]
    [InlineData(2601, true, "CLIENTE_CHAVE_REPETIDA")]
    [InlineData(229, true, "ESCRITA_RECUSADA")]
    [InlineData(229, false, "LEITURA_RECUSADA")]
    [InlineData(18456, false, "BANCO_LOGIN")]
    [InlineData(4060, false, "BANCO_SEM_ACESSO")]
    [InlineData(-2, false, "BANCO_SEM_RESPOSTA")]
    [InlineData(53, false, "BANCO_SEM_RESPOSTA")]
    [InlineData(8152, true, "BANCO_FALHOU")]
    public void Numero_do_erro_vira_codigo(int numero, bool gravando, string codigo) =>
        Assert.Equal(codigo, BancoSenior.Traduzir(numero, "texto do banco", gravando).Codigo);

    [Fact]
    public void Coluna_obrigatoria_leva_so_o_nome_da_coluna()
    {
        var e = BancoSenior.Traduzir(515, "Cannot insert the value NULL into column 'codmot', table 'SENIOR.dbo.r038afa'; column does not allow nulls.", true);
        Assert.Equal("CLIENTE_COLUNA_OBRIGATORIA", e.Codigo);
        Assert.Equal("codmot", e.Detalhe["coluna"]);
    }

    [Fact]
    public void Chave_repetida_nao_leva_o_valor_da_chave()
    {
        var e = BancoSenior.Traduzir(2627, "Violation of PRIMARY KEY constraint 'PK_R038AFA'. The duplicate key value is (101, 1, 4521).", true);
        Assert.Empty(e.Detalhe);
        Assert.DoesNotContain("4521", e.Message);
    }

    [Theory]
    [InlineData("IABS #35474", "IABS #35474", "ESTA")]
    [InlineData("IABS #35474   ", "IABS #35474", "ESTA")]
    [InlineData("IABS HML #35474", "IABS #35474", "OUTRA_IABS")]
    [InlineData("IABS #1", "IABS #35474", "OUTRA_IABS")]
    [InlineData("atestado entregue no RH", "IABS #35474", null)]
    [InlineData(null, "IABS #35474", null)]
    public void De_quem_e_a_linha(string? obsafa, string marca, string? esperado) =>
        Assert.Equal(esperado, Marca.Classificar(obsafa, marca));

    [Fact]
    public void Valores_do_banco_para_o_json()
    {
        Assert.Equal("2026-11-20", OperacoesSenior.Valor(new DateTime(2026, 11, 20, 0, 0, 0)));
        Assert.Equal("1900-12-31", OperacoesSenior.Valor(new DateTime(1900, 12, 31)));
        Assert.Equal("Maria", OperacoesSenior.Valor("Maria     "));
        Assert.Null(OperacoesSenior.Valor(DBNull.Value));
        Assert.Equal(12345678901m, OperacoesSenior.Valor(12345678901m));
        Assert.Equal((short)101, OperacoesSenior.Valor((short)101));
    }

    [Fact]
    public void Conexao_sempre_cifrada_e_com_o_nome_do_integrador()
    {
        var b = new BancoSenior(new ConfiguracaoBanco { Servidor = "srv-senior", Porta = 1433, Banco = "SENIOR_TESTE", Usuario = "iabs_integrador" }, NullLogger.Instance);
        var texto = b.TextoConexao(confiarCertificado: false);
        Assert.Contains("Encrypt=True", texto, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Data Source=srv-senior,1433", texto);
        Assert.Contains("Application Name=\"Integrador IABS\"", texto);
        Assert.DoesNotContain("ApplicationIntent", texto, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("dbo", "[dbo].")]
    [InlineData("", "")]
    public void Prefixo_do_schema(string schema, string prefixo) =>
        Assert.Equal(prefixo, new BancoSenior(new ConfiguracaoBanco { Schema = schema }, NullLogger.Instance).Prefixo);

    [Fact]
    public void Schema_com_caractere_estranho_e_recusado() =>
        Assert.Throws<InvalidOperationException>(() => new BancoSenior(new ConfiguracaoBanco { Schema = "dbo]; DROP TABLE x; --" }, NullLogger.Instance).Prefixo);
}
