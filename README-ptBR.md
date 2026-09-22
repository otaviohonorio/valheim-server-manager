<p align="center">
  <img src="src/ValheimServerManager.App/Assets/AppIcon.png" width="96" alt="" />
</p>

# Valheim Server Manager

> 🇺🇸 [Read in English](README.md) — o inglês é a versão de referência deste documento.

Gerenciador de servidores dedicados de **Valheim 1.0** para Windows. Liga, desliga salvando o mundo,
alterna entre modo normal e criativo, faz backups verificados, restaura com segurança e edita
senha, modificadores e listas de jogadores — tudo por uma interface nativa do Windows 11, em
português, inglês ou espanhol.

Ele existe por causa de um incidente real: um servidor e o jogo abriram a mesma pasta de saves, um
save ficou pela metade e o Valheim gerou um mundo novo por cima da base
([relato completo, em inglês](docs/incident-2026-09-16.md)). Cada proteção do app vem dali.

## O que ele faz

| | |
|---|---|
| **Iniciar / parar com segurança** | O servidor roda sem janela (não há um "X" para fechar sem salvar). Parar envia Ctrl+C e espera o log confirmar o save. |
| **Modo criativo de verdade** | Um botão liga/desliga construção sem custo. O preset vai sempre na linha de comando, então desligar remove a chave do mundo — e o app confere no arquivo depois do primeiro save. |
| **Verificação antes de iniciar** | Save incompleto, chunk faltando, pasta compartilhada com o jogo (mesmo por atalho ou junção), porta ocupada, mundo em uso: o servidor nem sobe. Pasta de saves no OneDrive/Dropbox exige confirmação. Mundo inexistente só é criado com confirmação. |
| **Freio de emergência** | Se o log mostrar que o Valheim não achou os dados e começou a gerar outro mundo, o servidor é encerrado na hora, antes de gravar por cima. |
| **Backups verificados** | Antes de iniciar e depois de parar (e quando você quiser). Cada backup guarda um save completo, conferido por SHA-256, com manifesto e retenção. |
| **Restauração segura** | Copia o mundo atual antes (ou guarda em quarentena), move a pasta antiga para `_substituidos`, nunca mistura arquivos. |
| **Configuração completa** | Nome, senha (criptografada com DPAPI), porta, público, crossplay, pastas, intervalo de save, backups do jogo, presets e todos os modificadores. |
| **Jogadores** | Quem está online agora (nome, Steam ID e desde quando), avisos de entrada e saída, e admin, banidos e permitidos com os nomes lidos do próprio mundo. |
| **Log ao vivo** | Eventos importantes filtrados, e o log de cada sessão arquivado. |
| **Desligar o PC sem perder nada** | Se o Windows for desligado com servidores rodando, o app segura o desligamento por alguns segundos e salva os mundos antes. |
| **Bandeja do sistema** | Fechar a janela com servidores rodando deixa o app na bandeja; notificações de jogadores e problemas aparecem por ali. |
| **Criar servidores** | Assistente "Novo servidor": mundo novo com a seed que você escolher (ou aleatória), ou cópia de um mundo que você já joga — só o último save completo, conferido. |
| **Vários servidores ao mesmo tempo** | Cada perfil tem seu mundo, pasta e porta, e vários podem rodar juntos. Travas impedem dois servidores no mesmo mundo/pasta ou na mesma porta (o Valheim usa a porta e a seguinte). Servidores abertos fora do app são detectados e podem ser adotados. |
| **Reparo de mundo duplicado** | Detecta objetos que o jogo gerou duas vezes (itens que "voltam", minério que quebra duas vezes, inimigos em dobro) e zonas que ele ainda vai gerar de novo; o Painel avisa e repara com backup antes. |
| **Manutenção automática** | A cada parada (inclusive no Reiniciar, antes de subir de novo), o gerenciador confere o mundo e corrige sozinho: duplicados, regiões por marcar e marcas de trapaça. Também dá para rodar quando quiser, em Mundo → "Conferir e corrigir agora". |
| **Marcas de trapaça** | Encontra o que o jogo marcou como "feito com trapaça" (itens marcados não empilham com os iguais) e tira a marca do mundo inteiro, sem alterar quantidades nem donos. |
| **Diagnóstico** | Inspeção do save e contagem de peças construídas por jogadores (bancadas, baús, portais…). |

## Instalação

1. Baixe o **`ValheimServerManager-Setup-x.y.z.exe`** na página de
   [Releases](https://github.com/otaviohonorio/valheim-server-manager/releases/latest).
2. Abra e siga o assistente: você escolhe a pasta de instalação e se quer atalho na Área de Trabalho.
   Não pede senha de administrador.
3. No primeiro uso, clique em **Criar meu primeiro servidor**. Se um servidor já estiver rodando por
   um `.bat`, o app o detecta e oferece adotá-lo.

Requer Windows 10 2004+ ou Windows 11 (x64) e o **Valheim Dedicated Server**, instalado pela Steam
(Biblioteca → Ferramentas). Não é preciso instalar .NET nem Windows App SDK.

O app segue o idioma do Windows (português, inglês ou espanhol; qualquer outro vira inglês). Dá
para trocar em **Proteções e sobre → Idioma**.

> **"O Windows protegeu o computador"?** O instalador ainda não tem assinatura digital, então o
> SmartScreen avisa nos primeiros downloads. Clique em **Mais informações → Executar assim mesmo**.
> O arquivo `.sha256` ao lado do instalador na página de Releases permite conferir que ele é o original.

**Atualizar** é só rodar o instalador novo: ele usa a mesma pasta, mantém tudo e os servidores
ligados continuam ligados (feche o app pela bandeja com "Sair e deixar rodando" antes).
**Desinstalar** fica em Configurações → Aplicativos. Nenhum dos dois mexe em mundos, backups ou
configurações (`%LOCALAPPDATA%\ValheimServerManager`), e a desinstalação só acontece com os servidores
desligados, para que nenhum fique rodando sem ter como salvar.

### Jogo e servidor nunca dividem o mesmo mundo

Cada servidor criado pelo app tem a **própria pasta de saves**, separada da pasta do jogo
(`AppData\LocalLow\IronGate\Valheim`). O app recusa usar a pasta do jogo, inclusive por atalhos,
links ou junções que apontem para ela, e avisa se a pasta estiver no OneDrive, Dropbox ou Google Drive,
que travam arquivos no meio do save.

Se você criar o servidor a partir de um mundo que já joga, ele recebe uma **cópia**. O mundo da
sua lista de mundos no jogo continua existindo, mas não recebe o que for feito no servidor. Para
jogar no mundo do servidor, mesmo sozinho, entre no servidor como os seus amigos (IP ou código de
entrada).

### Instalar a partir do código

`build/make-installer.ps1` gera o instalador (precisa do .NET SDK 10 e do
[Inno Setup 6](https://jrsoftware.org/isinfo.php)); `build/install.ps1 -Publish` instala direto,
sem instalador.

## Apoio

O app é gratuito e sempre vai ser. Se ele salvou o seu mundo (ou a sua paciência), há duas formas
de ajudar, e as duas pagam a mesma coisa — as horas de manutenção a cada atualização do Valheim:

- **[Ko-fi](https://ko-fi.com/ottorocket)** — uma contribuição única, de qualquer valor, **sem precisar de conta**.
- **[GitHub Sponsors](https://github.com/sponsors/otaviohonorio)** — recorrente, se preferir.

Não fazer nenhuma das duas não custa nada. Um bom relato de bug vale tanto quanto.

## Linha de comando

`cli\vsm.exe` faz o mesmo que o app, útil para agendar tarefas. Ele fala o mesmo idioma do
app (para forçar outro, use a variável de ambiente `VSM_LANG`: `en`, `pt-BR`, `es`).

```
vsm profiles                      lista perfis e servidores em execução
vsm create  --name X --password Y [--seed S | --copy-world <pasta>] [--port N] [--private]
vsm status  -p "Meu servidor"     estado do servidor e do mundo
vsm start   -p "Meu servidor"     inicia com todas as verificações
vsm stop    -p "Meu servidor"     Ctrl+C e espera o save
vsm restart -p "Meu servidor"     para, corrige o mundo e inicia de novo
vsm backup  -p "Meu servidor"     backup verificado (pode ser com o servidor rodando)
vsm backups / vsm restore --backup <nome>
vsm inspect <pasta-do-mundo> --pieces --duplicates --cheats
vsm clean-cheat-marks -p "Meu servidor"   tira a marca de trapaça do mundo
vsm clean-character <arquivo.fch> --apply   tira a marca dos itens na mochila de um personagem
vsm repair-world -p "Meu servidor"   remove objetos duplicados (servidor parado; backup antes)
vsm rebuild-index <pasta-do-mundo> --save-number N    recuperação de mundo
vsm e2e ...                       teste de ponta a ponta com o servidor real
```

## Desenvolvimento

- .NET 10 · WinUI 3 (Windows App SDK 2.4) · CommunityToolkit.Mvvm · Generic Host · Serilog
- `Core` sem dependência de interface, coberto por testes xUnit v3
- `dotnet build ValheimServerManager.slnx` · `dotnet test --project tests/ValheimServerManager.Core.Tests`
- `build/publish.ps1` gera a versão autocontida em `dist/`; `build/make-installer.ps1`, o instalador em `artifacts/installer/`
- Tag `vX.Y.Z` no GitHub → o workflow `release.yml` testa, gera e publica o instalador em Releases

Veja [docs/architecture.md](docs/architecture.md), as skills em `.claude/skills/` e o
[CHANGELOG](CHANGELOG.md). Para traduzir: cada área tem `Localization/<Nome>.resx` (inglês) com
`.pt-BR.resx` e `.es.resx` ao lado; um teste acusa chave faltando em qualquer idioma.

## Aviso

Projeto independente, sem relação com a Iron Gate ou a Coffee Stain. Valheim é marca dos seus
respectivos donos.

## Licença

[MIT](LICENSE). Valheim é marca da Iron Gate AB; este projeto não é afiliado nem endossado por ela.
