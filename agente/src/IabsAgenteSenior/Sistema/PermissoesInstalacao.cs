using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace IabsAgenteSenior.Sistema;

/// <summary>
/// O que o instalador faz como administrador para a janela abrir depois SEM o
/// controle de conta de usuário (pedido de 26/11/2026): dá a quem instalou
/// (o SID que o MSI passa, propriedade UserSID) permissão na pasta de dados e
/// para consultar, iniciar e parar o serviço. Os outros usuários da máquina
/// continuam sem acesso (a pasta tem a senha do banco, cifrada por DPAPI de
/// máquina: quem lê o arquivo consegue abrir). Os SIDs liberados ficam em
/// HKLM\SOFTWARE\IABS\AgenteSenior\Configuradores, para a proteção da pasta
/// não tirar o acesso deles depois.
/// </summary>
public static class PermissoesInstalacao
{
    private const string ChaveRegistro = @"SOFTWARE\IABS\AgenteSenior";
    private const string ValorConfiguradores = "Configuradores";

    /// <summary>Quem pode abrir a janela sem administrador (gravado pelo instalador).</summary>
    public static IReadOnlyList<SecurityIdentifier> Configuradores()
    {
        try
        {
            using var chave = Registry.LocalMachine.OpenSubKey(ChaveRegistro);
            if (chave?.GetValue(ValorConfiguradores) is not string[] lista) return [];
            var sids = new List<SecurityIdentifier>();
            foreach (var s in lista)
            {
                try
                {
                    sids.Add(new SecurityIdentifier(s));
                }
                catch (ArgumentException)
                {
                    // valor estranho no registro: ignora
                }
            }
            return sids;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// Chamado pelo MSI (custom action, como SYSTEM). Devolve as mensagens do
    /// que fez. Falha na pasta é erro (a instalação para); no serviço, aviso.
    /// </summary>
    public static List<string> Preparar(string pastaDados, string? sidUsuario)
    {
        var mensagens = new List<string>();
        var sids = Configuradores().ToList();
        if (!string.IsNullOrWhiteSpace(sidUsuario))
        {
            var novo = new SecurityIdentifier(sidUsuario.Trim()); // SID inválido = erro
            if (EhSistemaOuGrupoAmplo(novo))
            {
                mensagens.Add($"SID {novo} não é de uma pessoa: ignorado.");
            }
            else if (!sids.Contains(novo))
            {
                sids.Add(novo);
            }
        }
        using (var chave = Registry.LocalMachine.CreateSubKey(ChaveRegistro))
        {
            chave.SetValue(ValorConfiguradores, sids.Select(s => s.Value).ToArray(), RegistryValueKind.MultiString);
        }
        mensagens.Add(PastaDados.Proteger(pastaDados, sids));
        // a pasta do programa, que a TI pode ter escolhido fora de Program Files (2.1.4)
        mensagens.Add(PastaDados.ProtegerPastaDoPrograma(AppContext.BaseDirectory));
        foreach (var sid in sids)
        {
            try
            {
                LiberarServico(Constantes.NomeServico, sid);
                // o "Atualizar agora" da janela liga o atualizador (2.1.0)
                LiberarServico(Constantes.NomeServicoAtualizador, sid);
                mensagens.Add($"Serviço e atualizador: {sid} pode consultar, iniciar e parar.");
            }
            catch (Win32Exception ex)
            {
                mensagens.Add($"AVISO: não consegui liberar o serviço para {sid} ({ex.NativeErrorCode}).");
            }
        }
        // o integrador (conta virtual do serviço) liga o atualizador quando a IABS pede (2.1.0)
        try
        {
            var servico = (SecurityIdentifier)new NTAccount(Constantes.ContaServico).Translate(typeof(SecurityIdentifier));
            LiberarServico(Constantes.NomeServicoAtualizador, servico);
            mensagens.Add($"Atualizador: {Constantes.ContaServico} pode ligá-lo.");
        }
        catch (Exception ex) when (ex is Win32Exception or IdentityNotMappedException)
        {
            mensagens.Add($"AVISO: não consegui liberar o atualizador para o integrador ({ex.GetType().Name}).");
        }
        return mensagens;
    }

    /// <summary>
    /// Desinstalação de verdade (não atualização): apaga a lista de quem abre a
    /// janela sem administrador e a chave do registro, se ficar vazia.
    /// </summary>
    public static string Limpar()
    {
        try
        {
            using (var chave = Registry.LocalMachine.OpenSubKey(ChaveRegistro, writable: true))
            {
                chave?.DeleteValue(ValorConfiguradores, throwOnMissingValue: false);
            }
            using (var chave = Registry.LocalMachine.OpenSubKey(ChaveRegistro))
            {
                var vazia = chave is not null && chave.ValueCount == 0 && chave.SubKeyCount == 0;
                if (!vazia) return "Lista de configuradores removida do registro.";
            }
            Registry.LocalMachine.DeleteSubKey(ChaveRegistro, throwOnMissingSubKey: false);
            return "Lista de configuradores e chave do registro removidas.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return $"AVISO: não consegui limpar o registro ({ex.GetType().Name}).";
        }
    }

    /// <summary>Todo mundo, usuários, sistema e serviços não entram na lista (só pessoas).</summary>
    private static bool EhSistemaOuGrupoAmplo(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.WorldSid)
        || sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid)
        || sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid)
        || sid.IsWellKnown(WellKnownSidType.InteractiveSid)
        || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
        || sid.IsWellKnown(WellKnownSidType.AnonymousSid);

    // ---- permissões do serviço (sem chamar sc.exe: o integrador não abre outros processos) ----

    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceEnumerateDependents = 0x0008;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceInterrogate = 0x0080;
    private const uint ScManagerConnect = 0x0001;
    private const uint DaclSecurityInformation = 0x4;

    /// <summary>Acrescenta (sem tirar nada) a permissão de consultar, iniciar e parar o serviço para o SID.</summary>
    public static void LiberarServico(string nomeServico, SecurityIdentifier sid)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var servico = OpenService(scm, nomeServico, ReadControl | WriteDac);
            if (servico == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                QueryServiceObjectSecurity(servico, DaclSecurityInformation, null, 0, out var tamanho);
                var buffer = new byte[tamanho];
                if (!QueryServiceObjectSecurity(servico, DaclSecurityInformation, buffer, tamanho, out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                var sd = new RawSecurityDescriptor(buffer, 0);
                var dacl = sd.DiscretionaryAcl ?? new RawAcl(GenericAcl.AclRevision, 1);
                const uint mascara = ReadControl | ServiceQueryConfig | ServiceQueryStatus | ServiceEnumerateDependents
                                     | ServiceStart | ServiceStop | ServiceInterrogate;
                var jaTem = dacl.OfType<CommonAce>().Any(a =>
                    a.AceQualifier == AceQualifier.AccessAllowed && a.SecurityIdentifier == sid && ((uint)a.AccessMask & mascara) == mascara);
                if (jaTem) return;
                dacl.InsertAce(dacl.Count, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, (int)mascara, sid, false, null));
                sd.DiscretionaryAcl = dacl;
                var novo = new byte[sd.BinaryLength];
                sd.GetBinaryForm(novo, 0);
                if (!SetServiceObjectSecurity(servico, DaclSecurityInformation, novo))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                CloseServiceHandle(servico);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? maquina, string? banco, uint acesso);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr scm, string nome, uint acesso);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceObjectSecurity(IntPtr servico, uint informacao, byte[]? descritor, uint tamanho, out uint necessario);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceObjectSecurity(IntPtr servico, uint informacao, byte[] descritor);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
