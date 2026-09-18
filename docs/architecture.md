# Arquitetura

```
┌──────────────────────────────┐   ┌─────────────────────────┐
│ ValheimServerManager.App     │   │ ValheimServerManager.Cli│
│ WinUI 3 · MVVM · Generic Host│   │ System.CommandLine      │
│ Views → ViewModels → Services│   │ (vsm.exe)               │
└──────────────┬───────────────┘   └────────────┬────────────┘
               │                                │
               ▼                                ▼
┌──────────────────────────────────────────────────────────────┐
│ ValheimServerManager.Core   (sem UI, testado)                │
│                                                              │
│  Servers   ServerManager ──► ServerController (por perfil)   │
│               │                 │  pré-checagem · backup     │
│               │                 │  lançamento · log · parada │
│  Settings  JsonSettingsStore    │  emergência                │
│  Profiles  ServerProfile · LaunchArguments · ProfileValidator│
│            ModifierCatalog · BatchFileImporter               │
│  Processes HiddenConsoleLauncher · ConsoleSignal (Ctrl+C)    │
│            WmiServerProcessLocator                           │
│  Logs      ServerLogParser · LogTailer · LogArchiver         │
│  Backups   BackupService (SHA-256, manifesto, restauração)   │
│  Worlds    WorldInspector · ChunkIndex · WorldMetadata       │
│            ChunkIndexRebuilder · PlayerBuildScanner          │
│            WorldCreator (seed nova / cópia do último save)   │
│            ChunkObjects · WorldDatabase · WorldRepair        │
│            WorldCheatMarks · WorldSaveWriter                 │
└──────────────────────────────────────────────────────────────┘
               │
               ▼
      valheim_server.exe  (console oculto, -savedir, -logFile)
```

## Ciclo de vida de um servidor

```
Parado ──Iniciar──► pré-checagem ──bloqueio──► Parado (motivos na tela)
                        │ ok / confirmado
                        ▼
                  backup antes (se o save ainda não tem backup)
                        ▼
                  arquiva log · lança sem janela ──► Iniciando
                        ▼ "Game server connected"
                     Online ──"missing _main.N.db2"──► encerra na hora (emergência)
                        │
                     Parar: Ctrl+C por processo auxiliar ──► Desligando
                        ▼ processo sai
         save confirmado no log? ── sim ──► Parado (limpo) ──► backup depois ──► inspeção
                        │ não
                        ▼
                  Parado (aviso) · saída sem pedido ──► Caiu
```

## Decisões

- **Processo auxiliar para o Ctrl+C.** Anexar ao console de outro processo deixa o chamador num
  estado estranho; o próprio executável é relançado com `--vsm-send-ctrl-c <pid>` e sai.
- **Console oculto em vez de janela.** Mantém o comportamento de console (o Ctrl+C funciona)
  sem dar ao usuário um botão que mata o servidor sem salvar.
- **Servidores sobrevivem ao app.** Fechar o gerenciador não derruba nada; ao abrir de novo, o
  WMI encontra os processos pela linha de comando e o controlador se reanexa (lendo o log sem
  reagir a eventos antigos).
- **Backups só do save completo.** Arquivos de um save terminado nunca são reescritos pelo
  Valheim, então a cópia é consistente mesmo com o servidor rodando; restauração em pasta nova
  garante que nada se mistura.
- **Bandeja do sistema.** Com servidores rodando, fechar a janela esconde o app na bandeja; o
  ícone também entrega as notificações (as notificações nativas do Windows App SDK não funcionam
  em apps autocontidos).
- **Desligamento do Windows.** Processos de console ocultos são mortos sem salvar no logoff; o
  app intercepta `WM_ENDSESSION`, mostra um motivo na tela de desligamento e para cada servidor
  com Ctrl+C antes de liberar.
- **Senha com DPAPI** (escopo do usuário), configurações com escrita atômica, versão de schema e
  cópia `.bak`.
- **`TimeProvider`** injetado em tudo que espera ou marca tempo, para testes determinísticos.
