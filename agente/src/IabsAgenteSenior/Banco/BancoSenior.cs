using System.Data.Common;
using System.Text.RegularExpressions;
using IabsAgenteSenior.Configuracao;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace IabsAgenteSenior.Banco;

/// <summary>
/// Erro de uma operação, com o código do protocolo (docs/protocolo-canal.md).
/// A mensagem para a pessoa é escrita pela IABS; aqui só vão o código e, quando
/// ajuda a acertar o modelo, o nome da coluna ou da restrição (nunca dado).
/// </summary>
public sealed class ErroOperacao(string codigo, IReadOnlyDictionary<string, object?>? detalhe = null)
    : Exception(codigo)
{
    public string Codigo { get; } = codigo;
    public IReadOnlyDictionary<string, object?> Detalhe { get; } = detalhe ?? new Dictionary<string, object?>();
}

/// <summary>
/// Abre conexões com o SQL Server do Senior (pool do SqlClient: abrir por
/// operação é barato). A conexão é sempre cifrada e o certificado do servidor
/// é validado. Certificado que não é de uma autoridade reconhecida (o comum em
/// servidor de empresa) só passa com a caixa "Confiar no certificado do SQL
/// Server" marcada pela TI (2.1.4; antes o integrador passava a confiar sozinho).
/// </summary>
public sealed partial class BancoSenior(ConfiguracaoBanco cfg, ILogger logger)
{
    public ConfiguracaoBanco Config => cfg;

    /// <summary>A TI marcou "Confiar no certificado do SQL Server": a conexão é cifrada, sem validar o certificado.</summary>
    public bool CertificadoNaoValidado => cfg.ConfiarCertificadoServidor;

    /// <summary>Prefixo das tabelas: [schema]. (vazio = schema padrão do login).</summary>
    public string Prefixo
    {
        get
        {
            var valor = cfg.Schema;
            if (string.IsNullOrWhiteSpace(valor)) return "";
            if (!IdentificadorValido().IsMatch(valor))
            {
                throw new InvalidOperationException("Schema das tabelas inválido: use só letras, números e _.");
            }
            return $"[{valor}].";
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex IdentificadorValido();

    internal string TextoConexao(bool confiarCertificado)
    {
        var s = new SqlConnectionStringBuilder
        {
            DataSource = cfg.Porta is not null ? $"{cfg.Servidor},{cfg.Porta}"
                       : !string.IsNullOrEmpty(cfg.Instancia) ? $@"{cfg.Servidor}\{cfg.Instancia}"
                       : cfg.Servidor,
            InitialCatalog = cfg.Banco ?? "",
            IntegratedSecurity = cfg.AutenticacaoWindows,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = confiarCertificado,
            ApplicationName = Constantes.NomeExibicao,
            ConnectTimeout = 15,
            Pooling = true,
            MaxPoolSize = 4,
        };
        if (!cfg.AutenticacaoWindows)
        {
            s.UserID = cfg.Usuario ?? "";
            s.Password = cfg.Senha ?? "";
        }
        return s.ConnectionString;
    }

    /// <summary>
    /// Conexão aberta. Erro do banco vira ErroOperacao (código estável, sem o
    /// texto do banco). Certificado do SQL Server que não é de uma autoridade
    /// reconhecida = BANCO_CERTIFICADO: o integrador não passa a confiar nele
    /// sozinho (revisão de 27/11/2026); só com a caixa "Confiar no certificado
    /// do SQL Server" marcada pela TI na aba 2 (cifrado, sem validar).
    /// </summary>
    public async Task<SqlConnection> AbrirAsync(CancellationToken ct)
    {
        var conexao = new SqlConnection(TextoConexao(cfg.ConfiarCertificadoServidor));
        try
        {
            await conexao.OpenAsync(ct);
            return conexao;
        }
        catch (SqlException ex) when (!cfg.ConfiarCertificadoServidor && ErroDeCertificado(ex))
        {
            await conexao.DisposeAsync();
            logger.LogWarning("O certificado do SQL Server não é de uma autoridade reconhecida: a conexão não foi aberta (BANCO_CERTIFICADO).");
            throw new ErroOperacao("BANCO_CERTIFICADO");
        }
        catch (SqlException ex)
        {
            await conexao.DisposeAsync();
            throw Traduzir(ex.Number, ex.Message, gravando: false);
        }
        catch (InvalidOperationException)
        {
            await conexao.DisposeAsync();
            throw new ErroOperacao("BANCO_SEM_RESPOSTA");
        }
    }

    private static bool ErroDeCertificado(SqlException ex) =>
        ex.InnerException is System.Security.Authentication.AuthenticationException
        || ex.Message.Contains("certific", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("column '([A-Za-z0-9_]+)'")]
    private static partial Regex ColunaDoErro();

    [GeneratedRegex("constraint \"([A-Za-z0-9_]+)\"")]
    private static partial Regex RestricaoDoErro();

    /// <summary>
    /// Número do erro do SQL Server → código do protocolo. Da mensagem do banco
    /// só saem o nome da coluna ou da restrição (metadado, não dado de pessoa).
    /// </summary>
    public static ErroOperacao Traduzir(int numero, string mensagem, bool gravando) => numero switch
    {
        2627 or 2601 => new ErroOperacao("CLIENTE_CHAVE_REPETIDA"),
        515 => new ErroOperacao("CLIENTE_COLUNA_OBRIGATORIA", new Dictionary<string, object?> { ["coluna"] = ColunaDoErro().Match(mensagem) is { Success: true } m ? m.Groups[1].Value : null }),
        547 => new ErroOperacao("CLIENTE_REFERENCIA_INVALIDA", new Dictionary<string, object?> { ["restricao"] = RestricaoDoErro().Match(mensagem) is { Success: true } m ? m.Groups[1].Value : null }),
        229 or 230 => new ErroOperacao(gravando ? "ESCRITA_RECUSADA" : "LEITURA_RECUSADA"),
        18456 => new ErroOperacao("BANCO_LOGIN"),
        4060 => new ErroOperacao("BANCO_SEM_ACESSO"),
        -2 or 2 or 53 or 40 or 64 or 121 or 258 or 1231 or 10053 or 10054 or 10060 or 10061 or 11001 => new ErroOperacao("BANCO_SEM_RESPOSTA"),
        _ => new ErroOperacao("BANCO_FALHOU", new Dictionary<string, object?> { ["numero"] = numero }),
    };

    /// <summary>Erro de banco em qualquer comando (DbException genérica sem número vira BANCO_FALHOU).</summary>
    public static ErroOperacao Traduzir(DbException ex, bool gravando) =>
        ex is SqlException sql ? Traduzir(sql.Number, sql.Message, gravando) : new ErroOperacao("BANCO_FALHOU");
}
