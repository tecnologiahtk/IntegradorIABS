using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace IabsAgenteSenior.Sistema;

/// <summary>Permissões e remoção da pasta de dados (C:\ProgramData\IABS\AgenteSenior).</summary>
public static class PastaDados
{
    private static readonly SecurityIdentifier Sistema = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administradores = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Usuarios = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private const InheritanceFlags Heranca = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>
    /// Deixa a pasta só para SYSTEM, administradores, a conta do serviço e quem
    /// o instalador liberou (configuradores: quem instalou abre a janela sem o
    /// controle de conta de usuário), sem herdar as permissões de C:\ProgramData.
    /// Como administrador (o instalador roda como SYSTEM; revisão de 27/11/2026):
    /// o dono passa a ser os administradores (o dono sempre pode mudar as
    /// permissões), a pasta que já existia com dono de fora ou como junção é
    /// afastada e criada de novo (alguém a criou antes da instalação para ficar
    /// dono e ler a senha do banco) e C:\ProgramData\IABS fica só para SYSTEM e
    /// administradores gravarem. Devolve a mensagem para a tela.
    /// </summary>
    public static string Proteger(string pasta, IEnumerable<SecurityIdentifier>? configuradores = null)
    {
        var liberados = (configuradores ?? []).ToList();
        var comoAdministrador = EhAdministrador();
        string? afastada = null;
        if (comoAdministrador)
        {
            ProtegerMae(pasta);
            afastada = AfastarSeForDeOutro(pasta, liberados);
        }

        var info = Directory.CreateDirectory(pasta);
        var seguranca = new DirectorySecurity();
        seguranca.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        seguranca.AddAccessRule(new FileSystemAccessRule(Sistema, FileSystemRights.FullControl, Heranca, PropagationFlags.None, AccessControlType.Allow));
        seguranca.AddAccessRule(new FileSystemAccessRule(Administradores, FileSystemRights.FullControl, Heranca, PropagationFlags.None, AccessControlType.Allow));
        if (comoAdministrador) seguranca.SetOwner(Administradores);
        foreach (var sid in liberados)
        {
            seguranca.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify, Heranca, PropagationFlags.None, AccessControlType.Allow));
        }

        string mensagem;
        try
        {
            var servico = (SecurityIdentifier)new NTAccount(Constantes.ContaServico).Translate(typeof(SecurityIdentifier));
            seguranca.AddAccessRule(new FileSystemAccessRule(servico, FileSystemRights.Modify, Heranca, PropagationFlags.None, AccessControlType.Allow));
            mensagem = $"Pasta de dados protegida: SYSTEM, administradores, {Constantes.ContaServico}"
                       + (liberados.Count > 0 ? $" e {liberados.Count} usuário(s) liberado(s) na instalação." : ".");
        }
        catch (IdentityNotMappedException)
        {
            mensagem = $"A conta {Constantes.ContaServico} ainda não existe (serviço não instalado). A pasta ficou só para SYSTEM e administradores.";
        }
        info.SetAccessControl(seguranca);
        if (comoAdministrador)
        {
            var reparo = RepararHeranca(pasta);
            if (reparo.Count > 0) mensagem = $"{mensagem} {string.Join(" ", reparo)}";
        }
        return afastada is null ? mensagem : $"{afastada} {mensagem}";
    }

    /// <summary>
    /// A pasta do programa (2.1.4): a TI pode escolher uma fora de Program Files,
    /// que herdaria as permissões do disco (em C:\, qualquer usuário altera) e
    /// deixaria trocar o executável que roda como serviço. Fica só para SYSTEM e
    /// administradores alterarem, com dono administradores; os usuários leem e
    /// executam (o ícone e a janela). Os arquivos herdam (o instalador não põe
    /// permissão própria neles). Só como administrador.
    /// </summary>
    public static string ProtegerPastaDoPrograma(string pasta)
    {
        var info = new DirectoryInfo(pasta);
        if (!info.Exists) return $"AVISO: a pasta do programa {pasta} não existe.";
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"A pasta do programa {pasta} é uma junção ou atalho: escolha outra.");
        var seguranca = new DirectorySecurity();
        seguranca.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        seguranca.AddAccessRule(new FileSystemAccessRule(Sistema, FileSystemRights.FullControl, Heranca, PropagationFlags.None, AccessControlType.Allow));
        seguranca.AddAccessRule(new FileSystemAccessRule(Administradores, FileSystemRights.FullControl, Heranca, PropagationFlags.None, AccessControlType.Allow));
        seguranca.AddAccessRule(new FileSystemAccessRule(Usuarios, FileSystemRights.ReadAndExecute, Heranca, PropagationFlags.None, AccessControlType.Allow));
        seguranca.SetOwner(Administradores);
        info.SetAccessControl(seguranca);
        return $"Pasta do programa protegida: {pasta} (SYSTEM e administradores alteram; usuários só leem e executam).";
    }

    /// <summary>Administrador de verdade (token elevado) ou SYSTEM: pode trocar o dono.</summary>
    private static bool EhAdministrador()
    {
        using var eu = WindowsIdentity.GetCurrent();
        return eu.IsSystem || new WindowsPrincipal(eu).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool DonoConfiavel(SecurityIdentifier? dono, IReadOnlyList<SecurityIdentifier> liberados) =>
        dono is not null && (dono == Sistema || dono == Administradores || dono == TrustedInstaller || liberados.Contains(dono) || dono == ContaDoServico());

    /// <summary>
    /// NT SERVICE\IabsAgenteSenior (2.2.1): o serviço das versões antigas criava
    /// a pasta de dados (o log) antes de o instalador criá-la, e ficava dono dela.
    /// </summary>
    private static SecurityIdentifier? ContaDoServico()
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(Constantes.ContaServico).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }

    /// <summary>
    /// A pasta que já existe com dono de fora (nem SYSTEM, nem administradores,
    /// nem quem o instalador liberou) ou como junção não é usada: a junção sai
    /// (sem tocar no destino) e a pasta vai para "...recusada-AAAAMMDDHHMMSS",
    /// só para administradores. Só a pasta muda de dono, nunca o que está dentro.
    /// Devolve a mensagem, ou null quando a pasta é a de sempre.
    /// </summary>
    private static string? AfastarSeForDeOutro(string pasta, IReadOnlyList<SecurityIdentifier> liberados)
    {
        var dir = new DirectoryInfo(pasta);
        if (!dir.Exists) return null;
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            dir.Delete(); // tira a junção, não o destino
            return $"A pasta de dados era uma junção ou atalho: removida e criada de novo.";
        }
        var dono = dir.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (DonoConfiavel(dono, liberados)) return null;

        // primeiro afasta, depois fecha (2.2.1). Até a 2.2.0 fechava antes: a
        // permissão fechada tirava a herança de tudo que estava dentro e, se o
        // afastamento falhava (o atualizador da 2.1.3 deixava o log do
        // instalador aberto ali dentro), a pasta de sempre ficava sem
        // permissão nenhuma. Falhou aqui: a instalação para sem mexer em nada.
        var destino = $"{Path.TrimEndingDirectorySeparator(pasta)}.recusada-{DateTime.Now:yyyyMMddHHmmss}";
        dir.MoveTo(destino);
        var afastada = new DirectoryInfo(destino);
        var posse = new DirectorySecurity();
        posse.SetOwner(Administradores);
        afastada.SetAccessControl(posse);
        var fechada = new DirectorySecurity();
        fechada.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        fechada.AddAccessRule(new FileSystemAccessRule(Sistema, FileSystemRights.FullControl, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        fechada.AddAccessRule(new FileSystemAccessRule(Administradores, FileSystemRights.FullControl, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        afastada.SetAccessControl(fechada);
        return $"A pasta de dados já existia com outro dono ({dono?.Value ?? "?"}): afastada para {destino} e criada de novo.";
    }

    /// <summary>
    /// C:\ProgramData\IABS (a mãe da pasta padrão): dono administradores e só
    /// SYSTEM e administradores gravam; os usuários só listam esta pasta. Assim
    /// ninguém de fora renomeia a pasta de dados para pôr uma junção no lugar.
    /// </summary>
    private static void ProtegerMae(string pasta)
    {
        var mae = Directory.GetParent(Path.TrimEndingDirectorySeparator(pasta));
        if (mae is null || !string.Equals(mae.Name, "IABS", StringComparison.OrdinalIgnoreCase)) return;
        var programData = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        if (!string.Equals(Path.TrimEndingDirectorySeparator(mae.Parent?.FullName ?? ""), programData, StringComparison.OrdinalIgnoreCase)) return;
        mae.Create();
        mae.Refresh();
        if (mae.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{mae.FullName} é uma junção ou atalho: confira a máquina antes de instalar.");
        var seguranca = new DirectorySecurity();
        seguranca.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        seguranca.AddAccessRule(new FileSystemAccessRule(Sistema, FileSystemRights.FullControl, Heranca, PropagationFlags.None, AccessControlType.Allow));
        seguranca.AddAccessRule(new FileSystemAccessRule(Administradores, FileSystemRights.FullControl, Heranca, PropagationFlags.None, AccessControlType.Allow));
        seguranca.AddAccessRule(new FileSystemAccessRule(Usuarios, FileSystemRights.ReadAndExecute, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        seguranca.SetOwner(Administradores);
        mae.SetAccessControl(seguranca);
    }

    /// <summary>
    /// Apaga a pasta inteira. O que estiver preso (antivírus lendo) fica marcado
    /// para apagar na próxima reinicialização. Devolve quantos itens ficaram.
    /// </summary>
    public static int Remover(string pasta)
    {
        var pendentes = RemoverPasta(pasta);
        RemoverMaeSeVazia(pasta);
        return pendentes;
    }

    /// <summary>C:\ProgramData\IABS sai junto quando não sobra nada de outro produto da IABS.</summary>
    private static void RemoverMaeSeVazia(string pasta)
    {
        var mae = Directory.GetParent(Path.TrimEndingDirectorySeparator(pasta));
        if (mae is null || !mae.Exists || !string.Equals(mae.Name, "IABS", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (!mae.EnumerateFileSystemInfos().Any()) mae.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // fica para depois: não trava a desinstalação
        }
    }

    /// <summary>
    /// Conserta o que perdeu a herança de permissões dentro da pasta de dados
    /// (2.2.1): até a 2.2.0, um afastamento que falhava (ver AfastarSeForDeOutro)
    /// deixava os arquivos sem permissão nenhuma, e o serviço não lia mais a
    /// configuração. Cada item sem permissão herdada passa a ter dono
    /// administradores (privilégio de tomar posse) e a herdar da pasta. Junção,
    /// atalho e arquivo com mais de um nome (hard link) ficam como estão: mudar
    /// a permissão deles mudaria a de outro arquivo. Só como administrador
    /// (instalador); nunca para a instalação. Devolve as mensagens.
    /// </summary>
    internal static List<string> RepararHeranca(string pasta)
    {
        var mensagens = new List<string>();
        try
        {
            Privilegios.Ligar("SeTakeOwnershipPrivilege");
        }
        catch (Win32Exception ex)
        {
            return [$"AVISO: sem o privilégio de tomar posse ({ex.NativeErrorCode}): o que estiver sem permissão na pasta de dados fica como está."];
        }
        // ler os atributos de quem está sem permissão (contar os nomes do arquivo); sem ele, segue
        try
        {
            Privilegios.Ligar("SeBackupPrivilege");
        }
        catch (Win32Exception)
        {
        }

        var consertados = 0;
        var pastas = new Stack<string>();
        pastas.Push(pasta);
        while (pastas.Count > 0)
        {
            var dir = pastas.Pop();
            List<string> itens;
            try
            {
                itens = Directory.EnumerateFileSystemEntries(dir).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                mensagens.Add($"AVISO: não consegui listar {Path.GetRelativePath(pasta, dir)} ({ex.GetType().Name}).");
                continue;
            }
            foreach (var item in itens)
            {
                FileAttributes atributos;
                try
                {
                    atributos = File.GetAttributes(item);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (atributos.HasFlag(FileAttributes.ReparsePoint)) continue; // junção ou atalho: nem entra, nem mexe
                var ehPasta = atributos.HasFlag(FileAttributes.Directory);
                try
                {
                    if (SemHeranca(item, ehPasta))
                    {
                        if (!ehPasta && NomesDoArquivo(item) != 1)
                        {
                            mensagens.Add($"AVISO: {Path.GetRelativePath(pasta, item)} tem mais de um nome (hard link) e ficou como está.");
                            continue;
                        }
                        HerdarDaPasta(item, ehPasta);
                        consertados++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException or Win32Exception)
                {
                    mensagens.Add($"AVISO: não consegui consertar a permissão de {Path.GetRelativePath(pasta, item)} ({ex.GetType().Name}).");
                    continue;
                }
                if (ehPasta) pastas.Push(item);
            }
        }
        if (consertados > 0) mensagens.Insert(0, $"Permissões consertadas em {consertados} item(ns) da pasta de dados.");
        return mensagens;
    }

    /// <summary>Sem nenhuma permissão herdada da pasta (ou nem dá para ler a permissão).</summary>
    internal static bool SemHeranca(string item, bool ehPasta)
    {
        try
        {
            FileSystemSecurity s = ehPasta
                ? new DirectoryInfo(item).GetAccessControl(AccessControlSections.Access)
                : new FileInfo(item).GetAccessControl(AccessControlSections.Access);
            return !s.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(r => r.IsInherited);
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Dono administradores (o dono pode mudar a permissão) e nenhuma permissão própria: herda da pasta.</summary>
    private static void HerdarDaPasta(string item, bool ehPasta)
    {
        if (ehPasta)
        {
            var d = new DirectoryInfo(item);
            var posse = new DirectorySecurity();
            posse.SetOwner(Administradores);
            d.SetAccessControl(posse);
            var heranca = new DirectorySecurity();
            heranca.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            d.SetAccessControl(heranca);
        }
        else
        {
            var f = new FileInfo(item);
            var posse = new FileSecurity();
            posse.SetOwner(Administradores);
            f.SetAccessControl(posse);
            var heranca = new FileSecurity();
            heranca.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            f.SetAccessControl(heranca);
        }
    }

    /// <summary>Quantos nomes (hard links) o arquivo tem.</summary>
    private static uint NomesDoArquivo(string arquivo)
    {
        using var h = CreateFile(arquivo, LerAtributos, CompartilharTudo, IntPtr.Zero, AbrirExistente, AbrirOAtalho | ComoBackup, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandle(h, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return info.Nomes;
    }

    private const uint LerAtributos = 0x80;           // FILE_READ_ATTRIBUTES
    private const uint CompartilharTudo = 0x7;        // leitura, gravação e exclusão
    private const uint AbrirExistente = 3;            // OPEN_EXISTING
    private const uint AbrirOAtalho = 0x00200000;     // FILE_FLAG_OPEN_REPARSE_POINT
    private const uint ComoBackup = 0x02000000;       // FILE_FLAG_BACKUP_SEMANTICS

    [StructLayout(LayoutKind.Sequential)]
    private struct InfoDoArquivo
    {
        public uint Atributos;
        public uint Criacao1, Criacao2, Acesso1, Acesso2, Gravacao1, Gravacao2;
        public uint Volume, TamanhoAlto, TamanhoBaixo, Nomes, IndiceAlto, IndiceBaixo;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string nome, uint acesso, uint compartilhar, IntPtr seguranca, uint criacao, uint flags, IntPtr modelo);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle arquivo, out InfoDoArquivo info);

    /// <summary>Liga um privilégio que o processo já tem (ex.: SYSTEM no instalador).</summary>
    private static class Privilegios
    {
        private const uint AjustarPrivilegios = 0x20; // TOKEN_ADJUST_PRIVILEGES
        private const uint Consultar = 0x8;           // TOKEN_QUERY
        private const int Ligado = 0x2;               // SE_PRIVILEGE_ENABLED
        private const int NemTodosLigados = 1300;     // ERROR_NOT_ALL_ASSIGNED

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct UmPrivilegio
        {
            public int Quantos;
            public long Luid;
            public int Atributos;
        }

        public static void Ligar(string nome)
        {
            if (!OpenProcessToken(GetCurrentProcess(), AjustarPrivilegios | Consultar, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!LookupPrivilegeValue(null, nome, out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var p = new UmPrivilegio { Quantos = 1, Luid = luid, Atributos = Ligado };
                if (!AdjustTokenPrivileges(token, false, ref p, 0, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var erro = Marshal.GetLastWin32Error();
                if (erro == NemTodosLigados) throw new Win32Exception(erro);
            }
            finally
            {
                CloseHandle(token);
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr processo, uint acesso, out IntPtr token);

        [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue(string? sistema, string nome, out long luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool desligarTodos, ref UmPrivilegio novo, int tamanho, IntPtr anterior, IntPtr tamanhoAnterior);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    /// <summary>
    /// O que o atualizador da 2.1.3 ou anterior deixou na pasta de dados (2.2.1):
    /// a situação (atualizacao.json) e a pasta do instalador baixado, gravadas
    /// como SYSTEM e só com permissão de SYSTEM e administradores. O arquivo
    /// preso travava o botão Atualizar da janela e a situação do pedido da IABS.
    /// Chamado pelo instalador (SYSTEM). Junção ou atalho sai sem entrar nele.
    /// </summary>
    public static List<string> LimparRestosDaAtualizacaoAntiga(string pastaDados)
    {
        var mensagens = new List<string>();
        foreach (var nome in new[] { "atualizacao.json", "atualizacao.json.novo" })
        {
            var arquivo = Path.Combine(pastaDados, nome);
            try
            {
                if (!File.Exists(arquivo)) continue;
                File.Delete(arquivo); // atalho: sai o atalho, não o destino
                mensagens.Add($"Resto da atualização antiga removido: {nome}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                mensagens.Add($"AVISO: não consegui remover {nome} ({ex.GetType().Name}).");
            }
        }
        var pasta = Path.Combine(pastaDados, "atualizacao");
        try
        {
            var info = new DirectoryInfo(pasta);
            if (info.Exists)
            {
                // uma tentativa só: quando quem instala é o atualizador antigo, o
                // instalador e o log dele estão abertos aqui dentro; o que estiver
                // em uso sai na próxima instalação
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) info.Delete(); // só a junção
                else info.Delete(recursive: true);
                mensagens.Add("Resto da atualização antiga removido: pasta atualizacao.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            mensagens.Add($"AVISO: não consegui remover a pasta atualizacao ({ex.GetType().Name}).");
        }
        return mensagens;
    }

    private static int RemoverPasta(string pasta)
    {
        if (!Directory.Exists(pasta)) return 0;
        for (var tentativa = 0; tentativa < 5; tentativa++)
        {
            try
            {
                Directory.Delete(pasta, recursive: true);
                return 0;
            }
            catch (IOException)
            {
                Thread.Sleep(1000);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(1000);
            }
        }

        // sem entrar em junção ou atalho de pasta: só o que está dentro desta pasta sai
        var semDesvio = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        var pendentes = 0;
        foreach (var arquivo in Directory.EnumerateFiles(pasta, "*", semDesvio))
        {
            try
            {
                File.Delete(arquivo);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MoveFileEx(arquivo, null, MoverAoReiniciar);
                pendentes++;
            }
        }
        foreach (var dir in Directory.EnumerateDirectories(pasta, "*", semDesvio).OrderByDescending(d => d.Length).Append(pasta))
        {
            try
            {
                Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MoveFileEx(dir, null, MoverAoReiniciar);
            }
        }
        return pendentes;
    }

    private const int MoverAoReiniciar = 0x4; // MOVEFILE_DELAY_UNTIL_REBOOT

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existente, string? novo, int flags);
}
