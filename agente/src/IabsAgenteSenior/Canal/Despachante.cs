using System.Text.Json;
using IabsAgenteSenior.Atualizacao;
using IabsAgenteSenior.Banco;

namespace IabsAgenteSenior.Canal;

/// <summary>
/// Pedido da IABS → operação fixa. Só os nomes desta tabela existem; a
/// gravação só passa com a liberação marcada na janela do integrador. O
/// pedido de atualização (2.1.0) só deixa o pedido para o atualizador, que
/// confere tudo de novo antes de instalar.
/// </summary>
public sealed class Despachante(
    IOperacoesSenior operacoes,
    Func<bool> gravacaoLiberada,
    Func<PedidoAtualizacao, CancellationToken, Task>? pedirAtualizacao = null)
{
    private delegate Task<object> Operacao(Parametros p, CancellationToken ct);

    private static readonly HashSet<string> Gravacoes = ["lancar", "atualizar", "apagar", "trocar"];

    private Dictionary<string, Operacao> Tabela => new(StringComparer.Ordinal)
    {
        ["testar"] = async (_, ct) =>
        {
            var r = await operacoes.TestarAsync(ct);
            return new
            {
                banco = r.Banco.Nome,
                produto = r.Banco.Produto,
                versao = r.Banco.Versao,
                tabelas = r.Tabelas.Select(t => new { tabela = t.Tabela, ok = t.Ok, codigo = t.Codigo }),
                permissao = new { incluir = r.Incluir, alterar = r.Alterar, excluir = r.Excluir },
                gravacao = gravacaoLiberada(),
            };
        },
        ["situacoes"] = async (p, ct) => new { linhas = await operacoes.SituacoesAsync(p.Codigos("empresas", 99_999), p.Codigos("tipos", 99), ct) },
        ["colaboradores"] = async (p, ct) => new
        {
            linhas = await operacoes.ColaboradoresAsync(
                p.Codigos("empresas", 99_999), p.Codigos("tipos", 99), p.Texto("setor_de", 20), p.PessoaOpcional("depois"), p.Cpf(), ct),
        },
        ["afastamentos"] = async (p, ct) =>
        {
            var (ini, fim) = p.Periodo();
            return new { linhas = await operacoes.AfastamentosAsync(p.Pessoa(), ini, fim, p.Marca(), ct) };
        },
        ["marca_na_pessoa"] = async (p, ct) => new { linhas = await operacoes.MarcaNaPessoaAsync(p.Pessoa(), p.Marca(), ct) },
        ["marca_nas_empresas"] = async (p, ct) => new { linhas = await operacoes.MarcaNasEmpresasAsync(p.Marca(), p.Codigos("empresas", 99_999), ct) },
        ["linha_existe"] = async (p, ct) => new { existe = await operacoes.LinhaExisteAsync(p.Chave(), p.Marca(), ct) },
        ["lancar"] = async (p, ct) => new { afetadas = await operacoes.LancarAsync(p.NovaAqui(), p.Marca(), ct) },
        ["atualizar"] = async (p, ct) => new
        {
            afetadas = await operacoes.AtualizarAsync(p.Chave(), p.Marca(), p.Data("fim"), p.Inteiro("situacao", 1, 9_999), ct),
        },
        ["apagar"] = async (p, ct) => new { afetadas = await operacoes.ApagarAsync(p.Chave(), p.Marca(), ct) },
        ["trocar"] = async (p, ct) => new { afetadas = await operacoes.TrocarAsync(p.Chaves("apagar"), p.Nova(), p.Marca(), ct) },
        ["atualizar_integrador"] = async (p, ct) =>
        {
            var versao = p.Texto("versao", 20);
            var sha256 = p.Texto("sha256", 64);
            if (!RegrasAtualizacao.VersaoValida(versao)) throw Invalido("versao");
            if (!RegrasAtualizacao.Sha256Valido(sha256)) throw Invalido("sha256");
            if (!RegrasAtualizacao.MaisNova(versao, Constantes.Versao)) throw new ErroOperacao("ATUALIZACAO_DESNECESSARIA");
            if (pedirAtualizacao is null) throw new ErroOperacao("ATUALIZACAO_INDISPONIVEL");
            await pedirAtualizacao(new PedidoAtualizacao(versao, sha256, DateTimeOffset.Now), ct);
            return new { situacao = "AGENDADA", versao, atual = Constantes.Versao };
        },
    };

    private static ErroOperacao Invalido(string campo) =>
        new("PARAMETRO_INVALIDO", new Dictionary<string, object?> { ["campo"] = campo });

    /// <summary>As operações que esta versão sabe fazer (vão na mensagem "ola").</summary>
    public static IReadOnlyList<string> Nomes { get; } =
        ["testar", "situacoes", "colaboradores", "afastamentos", "marca_na_pessoa", "marca_nas_empresas", "linha_existe",
         "lancar", "atualizar", "apagar", "trocar", "atualizar_integrador"];

    public static bool Grava(string op) => Gravacoes.Contains(op);

    /// <summary>Executa a operação. Erro previsto sai como ErroOperacao; o resto, como INTEGRADOR_FALHOU.</summary>
    public async Task<object> ExecutarAsync(string op, JsonElement parametros, CancellationToken ct)
    {
        if (!Tabela.TryGetValue(op, out var operacao)) throw new ErroOperacao("OPERACAO_DESCONHECIDA");
        if (Grava(op) && !gravacaoLiberada()) throw new ErroOperacao("GRAVACAO_DESLIGADA");
        return await operacao(new Parametros(parametros), ct);
    }
}
