using System.Globalization;
using System.Text.Json;
using IabsAgenteSenior.Banco;

namespace IabsAgenteSenior.Canal;

/// <summary>
/// Leitura dos parâmetros de um pedido, com as faixas do Senior. Qualquer coisa
/// fora da regra vira PARAMETRO_INVALIDO com o nome do campo (nunca o valor).
/// </summary>
internal readonly struct Parametros(JsonElement raiz)
{
    private static ErroOperacao Invalido(string campo) =>
        new("PARAMETRO_INVALIDO", new Dictionary<string, object?> { ["campo"] = campo });

    private bool Tem(string campo, out JsonElement valor)
    {
        valor = default;
        return raiz.ValueKind == JsonValueKind.Object
               && raiz.TryGetProperty(campo, out valor)
               && valor.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }

    private static int Inteiro(JsonElement e, string campo, int minimo, int maximo)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) && n >= minimo && n <= maximo) return n;
        throw Invalido(campo);
    }

    public int Inteiro(string campo, int minimo, int maximo) =>
        Tem(campo, out var e) ? Inteiro(e, campo, minimo, maximo) : throw Invalido(campo);

    public string Texto(string campo, int maximo)
    {
        if (Tem(campo, out var e) && e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s && s.Length <= maximo) return s;
        throw Invalido(campo);
    }

    public string Marca()
    {
        var m = Texto("marca", 40);
        return Banco.Marca.Valida(m) ? m : throw Invalido("marca");
    }

    private static DateTime Data(JsonElement e, string campo)
    {
        if (e.ValueKind == JsonValueKind.String
            && DateTime.TryParseExact(e.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            && d.Year is >= 1900 and <= 2100)
        {
            return d;
        }
        throw Invalido(campo);
    }

    public DateTime Data(string campo) => Tem(campo, out var e) ? Data(e, campo) : throw Invalido(campo);

    /// <summary>Lista de códigos (empresas, tipos): 1 a 50 itens, sem repetição.</summary>
    public IReadOnlyList<int> Codigos(string campo, int maximo)
    {
        if (!Tem(campo, out var e) || e.ValueKind != JsonValueKind.Array) throw Invalido(campo);
        var lista = e.EnumerateArray().Select(x => Inteiro(x, campo, 1, maximo)).Distinct().ToList();
        return lista.Count is >= 1 and <= 50 ? lista : throw Invalido(campo);
    }

    private static Pessoa Pessoa(JsonElement e, string campo)
    {
        if (e.ValueKind != JsonValueKind.Object) throw Invalido(campo);
        var p = new Parametros(e);
        return new Pessoa(
            p.Inteiro("numemp", 1, 99_999),
            p.Inteiro("tipcol", 1, 99),
            p.Inteiro("numcad", 1, 999_999_999));
    }

    public Pessoa Pessoa(string campo = "pessoa") => Tem(campo, out var e) ? Pessoa(e, campo) : throw Invalido(campo);

    public Pessoa? PessoaOpcional(string campo) => Tem(campo, out var e) ? Pessoa(e, campo) : null;

    private static ChaveLinha Chave(JsonElement e, string campo)
    {
        if (e.ValueKind != JsonValueKind.Object) throw Invalido(campo);
        var p = new Parametros(e);
        return new ChaveLinha(Pessoa(e, campo), p.Data("datafa"), p.Inteiro("horafa", 0, 9_999));
    }

    public ChaveLinha Chave(string campo = "chave") => Tem(campo, out var e) ? Chave(e, campo) : throw Invalido(campo);

    public IReadOnlyList<ChaveLinha> Chaves(string campo)
    {
        if (!Tem(campo, out var e) || e.ValueKind != JsonValueKind.Array) throw Invalido(campo);
        var lista = e.EnumerateArray().Select(x => Chave(x, campo)).ToList();
        return lista.Count <= 20 ? lista : throw Invalido(campo);
    }

    /// <summary>Período: fim igual ou depois do início.</summary>
    public (DateTime Ini, DateTime Fim) Periodo()
    {
        var ini = Data("ini");
        var fim = Data("fim");
        return fim >= ini ? (ini, fim) : throw Invalido("fim");
    }

    public NovaLinha Nova(string campo = "nova")
    {
        if (!Tem(campo, out var e) || e.ValueKind != JsonValueKind.Object) throw Invalido(campo);
        return new Parametros(e).NovaAqui();
    }

    /// <summary>A linha nova com os campos no próprio objeto (lancar) ou dentro de "nova" (trocar).</summary>
    public NovaLinha NovaAqui()
    {
        var (ini, fim) = Periodo();
        return new NovaLinha(Pessoa(), ini, fim, Inteiro("situacao", 1, 9_999), Inteiro("atendente", 0, 999_999_999));
    }

    /// <summary>CPF opcional: até 11 dígitos (o Senior guarda como número, sem os zeros à esquerda).</summary>
    public long? Cpf()
    {
        if (!Tem("cpf", out var e)) return null;
        if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: >= 1 and <= 11 } s && s.All(char.IsAsciiDigit))
        {
            return long.Parse(s, CultureInfo.InvariantCulture);
        }
        throw Invalido("cpf");
    }
}
