using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace IabsAgenteSenior.Banco;

/// <summary>Colaborador no Senior: empresa, tipo e matrícula.</summary>
public readonly record struct Pessoa(int NumEmp, int TipCol, int NumCad);

/// <summary>Chave da linha na r038afa.</summary>
public readonly record struct ChaveLinha(Pessoa Pessoa, DateTime DatAfa, int HorAfa);

/// <summary>Linha nova de afastamento (dia inteiro).</summary>
public readonly record struct NovaLinha(Pessoa Pessoa, DateTime Ini, DateTime Fim, int Situacao, int Atendente);

/// <summary>Uma linha de resultado, com as colunas em minúsculas.</summary>
public sealed class Linha : Dictionary<string, object?>
{
    public Linha() : base(StringComparer.Ordinal) { }
}

/// <summary>Informação do banco para a mensagem "ola" e para a janela.</summary>
public sealed record InfoBanco(string Produto, string? Versao, string? Nome);

/// <summary>
/// As operações fixas do integrador (docs/protocolo-canal.md). O SQL está só
/// aqui: a IABS manda o nome da operação e os parâmetros, nunca SQL.
/// </summary>
public interface IOperacoesSenior
{
    Task<InfoBanco> InfoAsync(CancellationToken ct);
    Task<ResultadoTeste> TestarAsync(CancellationToken ct);
    Task<List<Linha>> SituacoesAsync(IReadOnlyList<int> empresas, IReadOnlyList<int> tipos, CancellationToken ct);
    Task<List<Linha>> ColaboradoresAsync(IReadOnlyList<int> empresas, IReadOnlyList<int> tipos, string setorDe, Pessoa? depois, long? cpf, CancellationToken ct);
    Task<List<Linha>> AfastamentosAsync(Pessoa pessoa, DateTime ini, DateTime fim, string marca, CancellationToken ct);
    Task<List<Linha>> MarcaNaPessoaAsync(Pessoa pessoa, string marca, CancellationToken ct);
    Task<List<Linha>> MarcaNasEmpresasAsync(string marca, IReadOnlyList<int> empresas, CancellationToken ct);
    Task<bool> LinhaExisteAsync(ChaveLinha chave, string marca, CancellationToken ct);
    Task<int> LancarAsync(NovaLinha nova, string marca, CancellationToken ct);
    Task<int> AtualizarAsync(ChaveLinha chave, string marca, DateTime fim, int situacao, CancellationToken ct);
    Task<int> ApagarAsync(ChaveLinha chave, string marca, CancellationToken ct);
    Task<int> TrocarAsync(IReadOnlyList<ChaveLinha> apagar, NovaLinha nova, string marca, CancellationToken ct);
}

public sealed record TabelaTestada(string Tabela, bool Ok, string? Codigo);

public sealed class ResultadoTeste
{
    public required InfoBanco Banco { get; init; }
    public List<TabelaTestada> Tabelas { get; } = [];
    public bool Incluir { get; set; }
    public bool Alterar { get; set; }
    public bool Excluir { get; set; }
    public bool CertificadoNaoValidado { get; set; }
    public bool LeituraOk => Tabelas.All(t => t.Ok);
    public bool GravacaoOk => Incluir && Alterar && Excluir;
}

/// <summary>A marca da IABS nas linhas que ela lança ("IABS #35474" ou "IABS HML #35474").</summary>
public static partial class Marca
{
    [GeneratedRegex(@"^IABS( [A-Z0-9]{1,8})? #\d{1,12}$")]
    private static partial Regex Padrao();

    public static bool Valida(string? marca) => marca is not null && Padrao().IsMatch(marca);

    /// <summary>De quem é a linha, pelo obsafa: "ESTA" (a marca pedida), "OUTRA_IABS" ou nulo (do RH ou do ponto).</summary>
    public static string? Classificar(string? obsafa, string marca)
    {
        var texto = obsafa?.Trim() ?? "";
        if (texto == marca) return "ESTA";
        return Valida(texto) ? "OUTRA_IABS" : null;
    }
}

/// <summary>Senior HCM (Vetorh) em SQL Server.</summary>
public sealed class OperacoesSenior(BancoSenior banco) : IOperacoesSenior
{
    public const int TamanhoDoBloco = 1000;

    /// <summary>Tabelas que a IABS usa (o teste confere que existem e são lidas; SELECT TOP 0: nenhum dado volta).</summary>
    public static readonly (string Tabela, string Coluna)[] TabelasDoTeste =
    [
        ("r034fun", "numcad"), ("r034cpl", "numcad"), ("r016orn", "nomloc"), ("r018ccu", "nomccu"),
        ("r024car", "titcar"), ("r030fil", "nomfil"), ("r006esc", "hormes"), ("r010sit", "dessit"), ("r038afa", "numcad"),
    ];

    private string P => banco.Prefixo;

    // ---- execução ----------------------------------------------------------------------
    private SqlCommand Comando(SqlConnection c, string sql, SqlTransaction? tx = null) =>
        new(sql, c, tx) { CommandTimeout = banco.Config.TempoLimiteConsultaSegundos };

    private static void Int(SqlCommand cmd, string nome, int valor) => cmd.Parameters.Add(nome, SqlDbType.Int).Value = valor;
    private static void Data(SqlCommand cmd, string nome, DateTime valor) => cmd.Parameters.Add(nome, SqlDbType.DateTime).Value = valor.Date;
    private static void Texto(SqlCommand cmd, string nome, string valor) => cmd.Parameters.Add(nome, SqlDbType.VarChar, 250).Value = valor;

    private static void PorPessoa(SqlCommand cmd, Pessoa p)
    {
        Int(cmd, "@e", p.NumEmp);
        Int(cmd, "@t", p.TipCol);
        Int(cmd, "@c", p.NumCad);
    }

    /// <summary>@x0, @x1, ... de uma lista de inteiros (já validados).</summary>
    private static string Lista(SqlCommand cmd, string prefixo, IReadOnlyList<int> valores)
    {
        var nomes = new List<string>(valores.Count);
        for (var i = 0; i < valores.Count; i++)
        {
            var nome = $"@{prefixo}{i}";
            Int(cmd, nome, valores[i]);
            nomes.Add(nome);
        }
        return string.Join(", ", nomes);
    }

    /// <summary>Valor do banco → valor do JSON: data vira AAAA-MM-DD, texto perde os espaços do fim.</summary>
    internal static object? Valor(object valor) => valor switch
    {
        DBNull => null,
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        string s => s.TrimEnd(),
        byte or short or int or long or decimal or double or float or bool => valor,
        _ => Convert.ToString(valor, CultureInfo.InvariantCulture),
    };

    private static async Task<List<Linha>> LerAsync(SqlCommand cmd, CancellationToken ct)
    {
        var linhas = new List<Linha>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var linha = new Linha();
            for (var i = 0; i < r.FieldCount; i++) linha[r.GetName(i).ToLowerInvariant()] = Valor(r.GetValue(i));
            linhas.Add(linha);
        }
        return linhas;
    }

    private async Task<T> NoBancoAsync<T>(bool gravando, Func<SqlConnection, Task<T>> fn, CancellationToken ct)
    {
        await using var c = await banco.AbrirAsync(ct);
        try
        {
            return await fn(c);
        }
        catch (DbException ex)
        {
            throw BancoSenior.Traduzir(ex, gravando);
        }
    }

    private static void ConferirMarca(string marca)
    {
        if (!Marca.Valida(marca)) throw new ErroOperacao("PARAMETRO_INVALIDO", new Dictionary<string, object?> { ["campo"] = "marca" });
    }

    // ---- leitura -----------------------------------------------------------------------
    public Task<InfoBanco> InfoAsync(CancellationToken ct) => NoBancoAsync(false, async c =>
    {
        await using var cmd = Comando(c, "SELECT DB_NAME() AS banco, CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)) AS versao");
        var l = (await LerAsync(cmd, ct)).FirstOrDefault();
        return new InfoBanco("SQL Server", l?["versao"] as string, l?["banco"] as string);
    }, ct);

    public async Task<ResultadoTeste> TestarAsync(CancellationToken ct)
    {
        var info = await InfoAsync(ct);
        var resultado = new ResultadoTeste { Banco = info, CertificadoNaoValidado = banco.CertificadoNaoValidado };
        await using var c = await banco.AbrirAsync(ct);
        foreach (var (tabela, coluna) in TabelasDoTeste)
        {
            try
            {
                await using var cmd = Comando(c, $"SELECT TOP 0 {coluna} FROM {P}{tabela}");
                await cmd.ExecuteNonQueryAsync(ct);
                resultado.Tabelas.Add(new(tabela, true, null));
            }
            catch (DbException ex)
            {
                resultado.Tabelas.Add(new(tabela, false, BancoSenior.Traduzir(ex, gravando: false).Codigo));
            }
        }
        try
        {
            await using var cmd = Comando(c, """
                SELECT HAS_PERMS_BY_NAME(@objeto, 'OBJECT', 'INSERT') AS incluir,
                       HAS_PERMS_BY_NAME(@objeto, 'OBJECT', 'UPDATE') AS alterar,
                       HAS_PERMS_BY_NAME(@objeto, 'OBJECT', 'DELETE') AS excluir
                """);
            Texto(cmd, "@objeto", string.IsNullOrWhiteSpace(banco.Config.Schema) ? "r038afa" : $"{banco.Config.Schema}.r038afa");
            var l = (await LerAsync(cmd, ct)).FirstOrDefault();
            resultado.Incluir = Convert.ToInt32(l?["incluir"] ?? 0, CultureInfo.InvariantCulture) == 1;
            resultado.Alterar = Convert.ToInt32(l?["alterar"] ?? 0, CultureInfo.InvariantCulture) == 1;
            resultado.Excluir = Convert.ToInt32(l?["excluir"] ?? 0, CultureInfo.InvariantCulture) == 1;
        }
        catch (DbException)
        {
            // sem a função HAS_PERMS_BY_NAME (não acontece no SQL Server 2005+): fica sem permissão
        }
        return resultado;
    }

    public Task<List<Linha>> SituacoesAsync(IReadOnlyList<int> empresas, IReadOnlyList<int> tipos, CancellationToken ct) => NoBancoAsync(false, async c =>
    {
        await using var cmd = Comando(c, "");
        cmd.CommandText = $"""
            SELECT f.sitafa AS codigo, MAX(s.dessit) AS descricao, COUNT(*) AS quantidade
            FROM {P}r034fun f LEFT JOIN {P}r010sit s ON s.codsit = f.sitafa
            WHERE f.numemp IN ({Lista(cmd, "e", empresas)}) AND f.tipcol IN ({Lista(cmd, "t", tipos)})
            GROUP BY f.sitafa
            ORDER BY f.sitafa
            """;
        return await LerAsync(cmd, ct);
    }, ct);

    /// <summary>
    /// Um bloco de até 1.000 cadastros, depois da chave (empresa, tipo, matrícula)
    /// informada (sem OFFSET: nada se repete nem se perde). Com cpf: só os
    /// cadastros desse CPF. Só sai o que a IABS usa (nada de PIS, CTPS, conta,
    /// endereço, deficiência ou estado civil).
    /// </summary>
    public Task<List<Linha>> ColaboradoresAsync(IReadOnlyList<int> empresas, IReadOnlyList<int> tipos, string setorDe, Pessoa? depois, long? cpf, CancellationToken ct) =>
        NoBancoAsync(false, async c =>
        {
            var (colunaSetor, joinSetor) = setorDe switch
            {
                "LOCAL" => ("o.nomloc", $"LEFT JOIN {P}r016orn o ON o.taborg = f.taborg AND o.numloc = f.numloc"),
                "CENTRO_CUSTO" => ("cc.nomccu", $"LEFT JOIN {P}r018ccu cc ON cc.numemp = f.numemp AND cc.codccu = f.codccu"),
                _ => throw new ErroOperacao("PARAMETRO_INVALIDO", new Dictionary<string, object?> { ["campo"] = "setor_de" }),
            };
            await using var cmd = Comando(c, "");
            Int(cmd, "@limite", TamanhoDoBloco);
            var continuacao = "";
            if (depois is { } d)
            {
                Int(cmd, "@ue", d.NumEmp);
                Int(cmd, "@ut", d.TipCol);
                Int(cmd, "@uc", d.NumCad);
                continuacao = "AND (f.numemp > @ue OR (f.numemp = @ue AND (f.tipcol > @ut OR (f.tipcol = @ut AND f.numcad > @uc))))";
            }
            if (cpf is { } numero)
            {
                cmd.Parameters.Add("@cpf", SqlDbType.BigInt).Value = numero;
                continuacao += " AND f.numcpf = @cpf";
            }
            cmd.CommandText = $"""
                SELECT TOP (@limite)
                  f.numemp, f.tipcol, f.numcad, f.numcpf, f.nomfun, f.datnas, f.tipsex, f.datadm, f.sitafa, f.datafa,
                  {colunaSetor} AS setor, car.titcar, fil.nomfil,
                  c.emapar, c.emacom, c.dddtel, c.numtel, c.nmddd2, c.nmtel2,
                  f.grains, f.valsal, e.hormes, e.horsem
                FROM {P}r034fun f
                LEFT JOIN {P}r034cpl c ON c.numemp = f.numemp AND c.tipcol = f.tipcol AND c.numcad = f.numcad
                {joinSetor}
                LEFT JOIN {P}r024car car ON car.estcar = f.estcar AND car.codcar = f.codcar
                LEFT JOIN {P}r030fil fil ON fil.numemp = f.numemp AND fil.codfil = f.codfil
                LEFT JOIN {P}r006esc e ON e.codesc = f.codesc
                WHERE f.numemp IN ({Lista(cmd, "e", empresas)}) AND f.tipcol IN ({Lista(cmd, "t", tipos)})
                  {continuacao}
                ORDER BY f.numemp, f.tipcol, f.numcad
                """;
            return await LerAsync(cmd, ct);
        }, ct);

    /// <summary>
    /// As linhas da pessoa que cruzam o período. O obsafa (texto livre do RH)
    /// não sai: vira só "ESTA", "OUTRA_IABS" ou nulo.
    /// </summary>
    public Task<List<Linha>> AfastamentosAsync(Pessoa pessoa, DateTime ini, DateTime fim, string marca, CancellationToken ct)
    {
        ConferirMarca(marca);
        return NoBancoAsync(false, async c =>
        {
            await using var cmd = Comando(c, $"""
                SELECT datafa, horafa, datter, sitafa, oriafa, obsafa
                FROM {P}r038afa
                WHERE numemp = @e AND tipcol = @t AND numcad = @c
                  AND datafa <= @fim AND (datter >= @ini OR datter <= '1900-12-31')
                """);
            PorPessoa(cmd, pessoa);
            Data(cmd, "@ini", ini);
            Data(cmd, "@fim", fim);
            var linhas = await LerAsync(cmd, ct);
            foreach (var l in linhas)
            {
                l["marca"] = Marca.Classificar(l["obsafa"] as string, marca);
                l.Remove("obsafa");
            }
            return linhas;
        }, ct);
    }

    public Task<List<Linha>> MarcaNaPessoaAsync(Pessoa pessoa, string marca, CancellationToken ct)
    {
        ConferirMarca(marca);
        return NoBancoAsync(false, async c =>
        {
            await using var cmd = Comando(c, $"""
                SELECT numemp, tipcol, numcad, datafa, horafa FROM {P}r038afa
                WHERE numemp = @e AND tipcol = @t AND numcad = @c AND obsafa = @marca
                """);
            PorPessoa(cmd, pessoa);
            Texto(cmd, "@marca", marca);
            return await LerAsync(cmd, ct);
        }, ct);
    }

    public Task<List<Linha>> MarcaNasEmpresasAsync(string marca, IReadOnlyList<int> empresas, CancellationToken ct)
    {
        ConferirMarca(marca);
        return NoBancoAsync(false, async c =>
        {
            await using var cmd = Comando(c, "");
            Texto(cmd, "@marca", marca);
            cmd.CommandText = $"SELECT numemp, tipcol, numcad, datafa, horafa FROM {P}r038afa WHERE obsafa = @marca AND numemp IN ({Lista(cmd, "m", empresas)})";
            return await LerAsync(cmd, ct);
        }, ct);
    }

    private string OndeALinha => "numemp = @e AND tipcol = @t AND numcad = @c AND datafa = @ini AND horafa = @hora AND obsafa = @marca";

    private static void Chave(SqlCommand cmd, ChaveLinha k, string marca)
    {
        PorPessoa(cmd, k.Pessoa);
        Data(cmd, "@ini", k.DatAfa);
        Int(cmd, "@hora", k.HorAfa);
        Texto(cmd, "@marca", marca);
    }

    public Task<bool> LinhaExisteAsync(ChaveLinha chave, string marca, CancellationToken ct)
    {
        ConferirMarca(marca);
        return NoBancoAsync(false, async c =>
        {
            await using var cmd = Comando(c, $"SELECT COUNT(*) FROM {P}r038afa WHERE {OndeALinha}");
            Chave(cmd, chave, marca);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
        }, ct);
    }

    // ---- gravação (só linhas com a marca da IABS) -------------------------------------------
    private async Task<int> InserirAsync(SqlConnection c, SqlTransaction? tx, NovaLinha n, string marca, CancellationToken ct)
    {
        await using var cmd = Comando(c, $"""
            INSERT INTO {P}r038afa (numemp, tipcol, numcad, datafa, horafa, datter, horter, sitafa, oriafa, staatu,
                                    contov, exmret, motalt, obsafa, codate, seqreg)
            VALUES (@e, @t, @c, @ini, 0, @fim, 0, @sit, 2, 2, 'N', 'N', 'N', @marca, @codate, 0)
            """, tx);
        PorPessoa(cmd, n.Pessoa);
        Data(cmd, "@ini", n.Ini);
        Data(cmd, "@fim", n.Fim);
        Int(cmd, "@sit", n.Situacao);
        Texto(cmd, "@marca", marca);
        Int(cmd, "@codate", n.Atendente);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<int> ApagarAsync(SqlConnection c, SqlTransaction? tx, ChaveLinha k, string marca, CancellationToken ct)
    {
        await using var cmd = Comando(c, $"DELETE FROM {P}r038afa WHERE {OndeALinha}", tx);
        Chave(cmd, k, marca);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<int> LancarAsync(NovaLinha nova, string marca, CancellationToken ct)
    {
        ConferirMarca(marca);
        return NoBancoAsync(true, c => InserirAsync(c, null, nova, marca, ct), ct);
    }

    public Task<int> AtualizarAsync(ChaveLinha chave, string marca, DateTime fim, int situacao, CancellationToken ct)
    {
        ConferirMarca(marca);
        return NoBancoAsync(true, async c =>
        {
            await using var cmd = Comando(c, $"UPDATE {P}r038afa SET datter = @fim, sitafa = @sit WHERE {OndeALinha}");
            Chave(cmd, chave, marca);
            Data(cmd, "@fim", fim);
            Int(cmd, "@sit", situacao);
            return await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public Task<int> ApagarAsync(ChaveLinha chave, string marca, CancellationToken ct)
    {
        ConferirMarca(marca);
        return NoBancoAsync(true, c => ApagarAsync(c, null, chave, marca, ct), ct);
    }

    /// <summary>Apaga as linhas da marca e lança a nova, tudo ou nada.</summary>
    public Task<int> TrocarAsync(IReadOnlyList<ChaveLinha> apagar, NovaLinha nova, string marca, CancellationToken ct)
    {
        ConferirMarca(marca);
        return NoBancoAsync(true, async c =>
        {
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
            try
            {
                var afetadas = 0;
                foreach (var k in apagar) afetadas += await ApagarAsync(c, tx, k, marca, ct);
                afetadas += await InserirAsync(c, tx, nova, marca, ct);
                await tx.CommitAsync(ct);
                return afetadas;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }, ct);
    }
}
