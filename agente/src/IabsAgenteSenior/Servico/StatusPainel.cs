using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using IabsAgenteSenior.Configuracao;

namespace IabsAgenteSenior.Servico;

/// <summary>
/// Situação do integrador para o ícone perto do relógio e para a janela: um
/// status.json na pasta de dados, regravado pelo serviço a cada 15 segundos,
/// com uma linha por conexão (2.2.0). Só datas, contagens e textos de
/// situação — nenhum dado de colaborador. O arquivo ganha leitura para
/// Usuários, porque o ícone roda sem administrador.
/// </summary>
public sealed record StatusAgente(
    string Versao,
    DateTimeOffset AtualizadoEm,
    IReadOnlyList<StatusConexao>? Conexoes,
    string? Mensagem)
{
    /// <summary>Sem conexão nenhuma (ou status da 2.1, sem a lista): aguardando configuração.</summary>
    [JsonIgnore] public bool Configurado => Conexoes is { Count: > 0 };

    [JsonIgnore] public IReadOnlyList<StatusConexao> Lista => Conexoes ?? [];
}

/// <summary>O canal de uma conexão com a IABS.</summary>
public sealed record StatusConexao(
    string Nome,
    bool Conectado,
    DateTimeOffset? ConectadoDesde,
    DateTimeOffset? DesconectadoDesde,
    DateTimeOffset? UltimoPedidoEm,
    long Pedidos,
    string? ConexaoIabs,
    bool GravacaoLiberada,
    string? Mensagem);

public static class StatusPainel
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Arquivo(Caminhos caminhos) => Path.Combine(caminhos.PastaDados, "status.json");

    public static void Gravar(Caminhos caminhos, StatusAgente status)
    {
        Directory.CreateDirectory(caminhos.PastaDados);
        var destino = Arquivo(caminhos);
        var temporario = destino + ".novo";
        File.WriteAllText(temporario, JsonSerializer.Serialize(status, Json));
        File.Move(temporario, destino, overwrite: true);
        try
        {
            var arquivo = new FileInfo(destino);
            var seguranca = arquivo.GetAccessControl();
            seguranca.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow));
            arquivo.SetAccessControl(seguranca);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            // sem a permissão extra, o ícone só mostra o estado do serviço
        }
    }

    public static StatusAgente? Ler(Caminhos caminhos)
    {
        try
        {
            var arquivo = Arquivo(caminhos);
            return File.Exists(arquivo) ? JsonSerializer.Deserialize<StatusAgente>(File.ReadAllText(arquivo), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
