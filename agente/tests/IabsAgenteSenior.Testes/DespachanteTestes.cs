using System.Text.Json;
using IabsAgenteSenior.Banco;
using IabsAgenteSenior.Canal;

namespace IabsAgenteSenior.Testes;

public class DespachanteTestes
{
    private readonly OperacoesFalsas _ops = new();

    private Task<object> Executar(string op, string json, bool gravacao = true) =>
        new Despachante(_ops, () => gravacao).ExecutarAsync(op, JsonDocument.Parse(json).RootElement, TestContext.Current.CancellationToken);

    private async Task<string> Codigo(string op, string json, bool gravacao = true) =>
        (await Assert.ThrowsAsync<ErroOperacao>(() => Executar(op, json, gravacao))).Codigo;

    private async Task<string?> Campo(string op, string json) =>
        (await Assert.ThrowsAsync<ErroOperacao>(() => Executar(op, json))).Detalhe["campo"] as string;

    private const string PessoaJson = """{"numemp":101,"tipcol":1,"numcad":4521}""";
    private const string ChaveJson = """{"numemp":101,"tipcol":1,"numcad":4521,"datafa":"2026-11-20","horafa":0}""";

    [Fact]
    public async Task Operacao_que_nao_existe_e_recusada() =>
        Assert.Equal("OPERACAO_DESCONHECIDA", await Codigo("DROP TABLE r038afa", "{}"));

    [Theory]
    [InlineData("lancar")]
    [InlineData("atualizar")]
    [InlineData("apagar")]
    [InlineData("trocar")]
    public async Task Gravacao_sem_liberacao_e_recusada_antes_de_tocar_no_banco(string op)
    {
        Assert.Equal("GRAVACAO_DESLIGADA", await Codigo(op, "{}", gravacao: false));
        Assert.Empty(_ops.Chamadas);
    }

    [Fact]
    public async Task Leitura_funciona_sem_liberacao_de_gravacao()
    {
        await Executar("situacoes", """{"empresas":[101,102,101],"tipos":[1]}""", gravacao: false);
        Assert.Equal([101, 102], _ops.UltimasEmpresas);
    }

    [Theory]
    [InlineData("""{"empresas":[],"tipos":[1]}""", "empresas")]
    [InlineData("""{"empresas":[0],"tipos":[1]}""", "empresas")]
    [InlineData("""{"empresas":["101"],"tipos":[1]}""", "empresas")]
    [InlineData("""{"empresas":[101]}""", "tipos")]
    public async Task Listas_fora_da_regra(string json, string campo) =>
        Assert.Equal(campo, await Campo("situacoes", json));

    [Fact]
    public async Task Colaboradores_com_continuacao_e_cpf()
    {
        await Executar("colaboradores", $$"""{"empresas":[101],"tipos":[1],"setor_de":"LOCAL","depois":{{PessoaJson}},"cpf":"01234567890"}""");
        Assert.Equal(new Pessoa(101, 1, 4521), _ops.UltimoDepois);
        Assert.Equal(1234567890L, _ops.UltimoCpf);
    }

    [Theory]
    [InlineData("""{"cpf":"123.456.789-00"}""")]
    [InlineData("""{"cpf":"123456789012"}""")]
    [InlineData("""{"cpf":12345678900}""")]
    public async Task Cpf_so_com_digitos(string extra)
    {
        var json = """{"empresas":[101],"tipos":[1],"setor_de":"LOCAL",""" + extra[1..];
        Assert.Equal("cpf", await Campo("colaboradores", json));
    }

    [Theory]
    [InlineData("IABS #35474", true)]
    [InlineData("IABS HML #35474", true)]
    [InlineData("IABS #", false)]
    [InlineData("IABS hml #1", false)]
    [InlineData("Atestado do RH", false)]
    [InlineData("IABS #1' OR 1=1 --", false)]
    [InlineData("", false)]
    public async Task So_aceita_a_marca_da_iabs(string marca, bool aceita)
    {
        var json = $$"""{"pessoa":{{PessoaJson}},"ini":"2026-11-20","fim":"2026-11-21","situacao":14,"atendente":0,"marca":{{JsonSerializer.Serialize(marca)}}}""";
        if (aceita)
        {
            await Executar("lancar", json);
            Assert.Equal(new NovaLinha(new Pessoa(101, 1, 4521), new DateTime(2026, 11, 20), new DateTime(2026, 11, 21), 14, 0), _ops.UltimaNova);
        }
        else
        {
            Assert.Equal("marca", await Campo("lancar", json));
        }
    }

    [Theory]
    [InlineData("\"ini\":\"2026-11-21\",\"fim\":\"2026-11-20\"", "fim")]
    [InlineData("\"ini\":\"20/11/2026\",\"fim\":\"2026-11-20\"", "ini")]
    [InlineData("\"ini\":\"1899-12-31\",\"fim\":\"2026-11-20\"", "ini")]
    public async Task Periodo_fora_da_regra(string datas, string campo)
    {
        var json = $$"""{"pessoa":{{PessoaJson}},{{datas}},"situacao":14,"atendente":0,"marca":"IABS #1"}""";
        Assert.Equal(campo, await Campo("lancar", json));
    }

    [Fact]
    public async Task Pessoa_fora_da_faixa() =>
        Assert.Equal("numcad", await Campo("marca_na_pessoa", """{"pessoa":{"numemp":101,"tipcol":1,"numcad":0},"marca":"IABS #1"}"""));

    [Fact]
    public async Task Trocar_apaga_as_chaves_e_lanca_a_nova()
    {
        var json = $$"""{"apagar":[{{ChaveJson}}],"nova":{"pessoa":{{PessoaJson}},"ini":"2026-11-22","fim":"2026-11-23","situacao":14,"atendente":7},"marca":"IABS HML #9"}""";
        await Executar("trocar", json);
        Assert.Equal(new DateTime(2026, 11, 22), _ops.UltimaNova!.Value.Ini);
        Assert.Equal(7, _ops.UltimaNova!.Value.Atendente);
    }

    [Fact]
    public async Task Erro_do_banco_passa_com_o_codigo()
    {
        _ops.Falhar = new ErroOperacao("BANCO_LOGIN");
        Assert.Equal("BANCO_LOGIN", await Codigo("testar", "{}"));
    }
}
