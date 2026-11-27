using IabsAgenteSenior.Banco;

namespace IabsAgenteSenior.Testes;

/// <summary>Operações sem banco: devolvem dados fixos e guardam o que receberam.</summary>
internal sealed class OperacoesFalsas : IOperacoesSenior
{
    public List<string> Chamadas { get; } = [];
    public NovaLinha? UltimaNova { get; private set; }
    public IReadOnlyList<int>? UltimasEmpresas { get; private set; }
    public Pessoa? UltimoDepois { get; private set; }
    public long? UltimoCpf { get; private set; }
    public ErroOperacao? Falhar { get; set; }
    /// <summary>Quanto o "testar" demora (para encher a fila do canal).</summary>
    public TimeSpan Demora { get; set; }

    private T Registrar<T>(string nome, T valor)
    {
        Chamadas.Add(nome);
        if (Falhar is not null) throw Falhar;
        return valor;
    }

    private static Linha L(params (string, object?)[] colunas)
    {
        var l = new Linha();
        foreach (var (k, v) in colunas) l[k] = v;
        return l;
    }

    public Task<InfoBanco> InfoAsync(CancellationToken ct) => Task.FromResult(Registrar("info", new InfoBanco("SQL Server", "15.0.2080.9", "SENIOR_TESTE")));

    public async Task<ResultadoTeste> TestarAsync(CancellationToken ct)
    {
        if (Demora > TimeSpan.Zero) await Task.Delay(Demora, ct);
        var r = new ResultadoTeste { Banco = new InfoBanco("SQL Server", "15.0.2080.9", "SENIOR_TESTE"), Incluir = true, Alterar = true, Excluir = true };
        r.Tabelas.Add(new TabelaTestada("r034fun", true, null));
        return Registrar("testar", r);
    }

    public Task<List<Linha>> SituacoesAsync(IReadOnlyList<int> empresas, IReadOnlyList<int> tipos, CancellationToken ct)
    {
        UltimasEmpresas = empresas;
        return Task.FromResult(Registrar("situacoes", new List<Linha> { L(("codigo", 1), ("descricao", "Trabalhando"), ("quantidade", 10)) }));
    }

    public Task<List<Linha>> ColaboradoresAsync(IReadOnlyList<int> empresas, IReadOnlyList<int> tipos, string setorDe, Pessoa? depois, long? cpf, CancellationToken ct)
    {
        UltimasEmpresas = empresas;
        UltimoDepois = depois;
        UltimoCpf = cpf;
        return Task.FromResult(Registrar("colaboradores", new List<Linha>
        {
            L(("numemp", 101), ("tipcol", 1), ("numcad", 7), ("datadm", "2020-01-02"), ("valsal", 1234.56m)),
        }));
    }

    public Task<List<Linha>> AfastamentosAsync(Pessoa pessoa, DateTime ini, DateTime fim, string marca, CancellationToken ct) =>
        Task.FromResult(Registrar("afastamentos", new List<Linha>()));

    public Task<List<Linha>> MarcaNaPessoaAsync(Pessoa pessoa, string marca, CancellationToken ct) =>
        Task.FromResult(Registrar("marca_na_pessoa", new List<Linha>()));

    public Task<List<Linha>> MarcaNasEmpresasAsync(string marca, IReadOnlyList<int> empresas, CancellationToken ct) =>
        Task.FromResult(Registrar("marca_nas_empresas", new List<Linha>()));

    public Task<bool> LinhaExisteAsync(ChaveLinha chave, string marca, CancellationToken ct) => Task.FromResult(Registrar("linha_existe", true));

    public Task<int> LancarAsync(NovaLinha nova, string marca, CancellationToken ct)
    {
        UltimaNova = nova;
        return Task.FromResult(Registrar("lancar", 1));
    }

    public Task<int> AtualizarAsync(ChaveLinha chave, string marca, DateTime fim, int situacao, CancellationToken ct) => Task.FromResult(Registrar("atualizar", 1));

    public Task<int> ApagarAsync(ChaveLinha chave, string marca, CancellationToken ct) => Task.FromResult(Registrar("apagar", 1));

    public Task<int> TrocarAsync(IReadOnlyList<ChaveLinha> apagar, NovaLinha nova, string marca, CancellationToken ct)
    {
        UltimaNova = nova;
        return Task.FromResult(Registrar("trocar", apagar.Count + 1));
    }
}
