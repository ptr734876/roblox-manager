<div align="center">

# 🎮 Roblox Account Manager

**Мульти-аккаунт лаунчер для Roblox на Windows (Мульти-окна • Авто-реконнект • Анти-АФК • Сетка окон)**  
**Multi-Account Launcher for Roblox on Windows (Multi-Instance • Auto-Reconnect • Anti-AFK • Window Tiling)**

[![Download Latest Release](https://img.shields.io/github/v/release/ptr734876/roblox-manager?label=%D0%A1%D0%BA%D0%B0%D1%87%D0%B0%D1%82%D1%8C%20%2F%20Download%20.EXE&style=for-the-badge&color=2563EB)](https://github.com/ptr734876/roblox-manager/releases/latest)
[![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011%20(x64)-0078D6?style=for-the-badge&logo=windows)](https://github.com/ptr734876/roblox-manager/releases/latest)
[![License](https://img.shields.io/badge/License-Apache%202.0-10B981?style=for-the-badge)](LICENSE)

[**🇷🇺 Русская инструкция**](#-русский-быстрый-старт-и-возможности) • [**🇬🇧 English Guide**](#-english-quick-start--features)

</div>

---

# 🇷🇺 Русский: Быстрый старт и возможности

**Roblox Account Manager** — это минималистичное портативное приложение для Windows, которое позволяет хранить несколько аккаунтов Roblox и запускать их одновременно в разных окнах в 1 клик. Вся работа происходит локально: без внедрения (инжектов) в память игры и без передачи ваших данных куда-либо.

## 🚀 Как скачать и начать пользоваться (за 1 минуту)

Никаких установщиков, архивов с десятками библиотек или ручной установки .NET — программа собрана в **один готовый `.exe` файл**:

1. Перейдите в раздел **[Releases (Последний релиз)](https://github.com/ptr734876/roblox-manager/releases/latest)**.
2. Скачайте файл **`RobloxAccountManager.exe`** (или архив `RobloxAccountManager-win-x64-portable.zip`).
3. Запустите **`RobloxAccountManager.exe`**.
   > 💡 *Если фильтр Windows SmartScreen покажет синее окно при первом запуске скачанного `.exe`, нажмите **«Подробнее»** → **«Выполнить в любом случае»**.*

---

## ✨ Основные возможности

- **🟢 Мульти-Roblox** — встроенный фоновый модуль автоматически освобождает системный мьютекс `ROBLOX_singletonEvent`, позволяя открывать сколько угодно окон Roblox параллельно без сторонних программ.
- **➕ Удобное добавление аккаунтов (2 способа):**
  - **Через встроенный браузер (`➕ Браузер`)** — откроется изолированное окно входа Roblox (WebView2). Как только вы войдёте в аккаунт (поддерживается 2FA и капча), программа сама перехватит и сохранит сессию.
  - **По токену (`🔑 Cookie`)** — мгновенное добавление аккаунта вставкой строки `.ROBLOSECURITY`.
- **🎯 Запуск куда угодно в 1 клик (`▶`) или пачкой (`🚀 Запустить`):**
  - **По Place ID или обычной ссылке** на игру Roblox;
  - **На Приватный (VIP) сервер** — поддерживаются как классические ссылки `privateServerLinkCode=...`, так и новые ссылки вида `roblox.com/share?code=...&type=Server`;
  - **К конкретному игроку по нику** — просто впишите `Ник` или `@Ник` во второе поле (даже без указания Place ID), и выбранные аккаунты зайдут прямо на сервер к этому игроку;
  - **По `JobId`** — вход на конкретный игровой сервер;
  - **На главную страницу клиента** — если оставить оба поля пустыми, откроется само приложение Roblox под выбранным аккаунтом.
- **🔄 Умный Авто-реконнект (`Авто-реконнект`):**
  - Программа в реальном времени следит за логами каждого запущенного окна.
  - При обрыве связи или кике (`Error 277`, `279`, `267`, `268`, `273` и др.) автоматически закрывает зависшее окно и перезаходит этим же аккаунтом в ту же игру/приватку.
  - **Защита внутриигровых телепортов:** во время штатных переходов между серверами внутри игры (`Reason 285` / `doTeleport`) программа не мешает игре.
  - При перезапуске самого менеджера он **автоматически подхватывает** уже открытые окна Roblox.
- **🛡 Безопасный Анти-АФК (`Анти-АФК`):**
  - Каждые `N` минут (по умолчанию 12 мин, настраивается) на долю секунды передаёт фокус каждому открытому окну Roblox и отправляет безопасное аппаратное нажатие `Right Ctrl`, затем мгновенно возвращает фокус вашему активному окну.
  - Персонаж не двигается, камера и инвентарь не сбиваются, но 20-минутный таймер бездействия гарантированно сбрасывается. Если вы прямо сейчас двигаете мышь или печатаете, импульс откладывается.
- **🗖 Автоматическая расстановка окон сеткой (`🗖 Сетка окон`):**
  - В 1 клик выстраивает все открытые окна Roblox равномерной плиткой на весь экран (не перекрывая панель задач Windows и обходя ограничение минимального размера окна Roblox `800×600`).
- **💻 Режим «Не спать (крышка)»:**
  - Одним чекбоксом запрещает Windows засыпать от бездействия и временно отключает переход ноутбука в сон при закрытии крышки. При снятии галочки или закрытии программы прежние настройки электропитания автоматически восстанавливаются.
- **⚙ Настройки графики, FPS и CPU (`⚙ Настройки`):**
  - Управление уровнем графики (`1–10` или `Авто`), громкостью, чувствительностью мыши и лимитом FPS (`15 / 30 / 60 / 120 / 144 / 240`) через штатный файл настроек `GlobalBasicSettings_13.xml`.
  - Функция **«Снижать CPU-приоритет фоновых окон»** автоматически переводит неактивные окна на пониженный приоритет (`BelowNormal`).
- **⬇ Автообновление с GitHub (`⬇ Обновить`) и компактный лог ошибок (`📋 Лог`):**
  - **Кнопка `⬇ Обновить`** проверяет наличие свежей версии на GitHub, скачивает новый `.exe` и автоматически перезапускает менеджер (не закрывая ваши открытые окна Roblox!).
  - **Кнопка `📋 Лог`** сохраняет компактный диагностический отчёт. В программу встроен кольцевой перехватчик ошибок (UI, фоновые потоки, асинхронные задачи и скрытые исключения) с жёстким лимитом в 200 записей — он не расходует оперативную память и автоматически вырезает любые куки перед записью.

---

## 🔒 Безопасность и хранение ваших Cookie

- Все токены `.ROBLOSECURITY` шифруются с помощью системного криптопровайдера **Windows DPAPI (`ProtectedData`, привязка `CurrentUser`)**.
- Зашифрованный файл конфигурации хранится **только локально на вашем ПК** по пути:
  ```text
  %APPDATA%\RobloxAccountManager\accounts_data.json
  ```
- Даже если кто-то скопирует файл `accounts_data.json` на другой компьютер или под другую учётную запись Windows, расшифровать куки будет **технически невозможно** — ключи шифрования хранятся в ядре вашей учётной записи Windows.

---

## 🛠 Системные требования (Windows)

- **ОС:** Windows 10 или Windows 11 (64-бит).
- **Установленный клиент Roblox** (`RobloxPlayerBeta.exe`).
- **Microsoft Edge WebView2 Runtime** — предустановлен в Windows 10 и Windows 11.

<details>
<summary><b>👨‍💻 Сборка из исходного кода (для разработчиков)</b></summary>

```powershell
git clone https://github.com/ptr734876/roblox-manager.git
cd roblox-manager

# Запуск
dotnet run -c Release

# Сборка полностью автономного одиночного .exe (не требующего установки .NET)
dotnet publish RobloxAccountManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```
</details>

---
---

# 🇬🇧 English: Quick Start & Features

**Roblox Account Manager** is a clean, minimalist, standalone Windows desktop application for managing multiple Roblox accounts and launching multiple game instances simultaneously in one click. All data stays strictly on your PC — no memory injection, no DLL exploits, and no external servers.

## 🚀 Download & Run (Zero Setup)

You do **not** need to install .NET or extract complex folders — the application is packaged as a **self-contained single-file `.exe`**:

1. Go to the **[Latest Release](https://github.com/ptr734876/roblox-manager/releases/latest)** page.
2. Download **`RobloxAccountManager.exe`** (or `RobloxAccountManager-win-x64-portable.zip`).
3. Run **`RobloxAccountManager.exe`**.
   > 💡 *If Windows SmartScreen shows a prompt on first launch, click **“More info”** → **“Run anyway”**.*

---

## ✨ Key Features

- **🟢 Built-in Multi-Roblox (`Мульти-Roblox`)** — automatically closes the `ROBLOX_singletonEvent` handle in background processes so you can run as many Roblox windows side-by-side as your hardware allows.
- **➕ Easy Account Addition (2 Methods):**
  - **Built-in Browser (`➕ Браузер`)** — log in through an isolated WebView2 window (supports 2FA & Captcha). Once logged in, the app automatically captures and encrypts your session.
  - **Direct Cookie (`🔑 Cookie`)** — paste your `.ROBLOSECURITY` cookie directly.
- **🎯 Flexible 1-Click (`▶`) & Batch Launch (`🚀 Запустить`):**
  - **Place ID or Game URL** — join any public Roblox game;
  - **Private (VIP) Server Links** — supports both legacy `privateServerLinkCode=...` URLs and modern `roblox.com/share?code=...` links;
  - **Join by Player Username** — type a player's `Username` or `@Username` in the second box (no Place ID needed) to join their server directly;
  - **Specific Server `JobId`** — connect to an exact server instance;
  - **Roblox Home App** — leave both target boxes empty to open the Roblox desktop client logged into the selected account.
- **🔄 Smart Auto-Reconnect (`Авто-реконнект`):**
  - Monitors each instance's log file in real time.
  - When a real disconnect or kick occurs (`Error 277`, `279`, `267`, `268`, `273`, etc.), it automatically closes the disconnected client and relaunches that specific account back into the same target.
  - **Teleport-Safe:** detects in-game server teleports (`Reason 285` / `doTeleport`) and grants a protection window so AFK-farm teleports are never interrupted.
  - **Session Re-Attach:** if you restart the manager while Roblox windows are already open, it automatically re-attaches to all running instances.
- **🛡 Non-Intrusive Anti-AFK (`Анти-АФК`):**
  - Periodically (default: every 12 minutes) briefly activates each Roblox window, sends a safe `Right Ctrl` hardware scan-code + window message to reset Roblox's 20-minute idle timer, and immediately restores focus to your previous window.
- **🗖 1-Click Window Grid Tiling (`🗖 Сетка окон`):**
  - Automatically arranges all open Roblox windows into an optimal grid across your screen without covering the Windows taskbar (bypasses Roblox's default `800×600` minimum window size limit).
- **💻 Prevent Sleep & Lid Close (`Не спать (крышка)`):**
  - Prevents Windows from sleeping on idle and temporarily sets laptop lid-close action to *"Do nothing"* while enabled. Automatically restores your original Windows power settings when unchecked or when the app closes.
- **⚙ Roblox Graphics, FPS Cap & CPU Priority (`⚙ Настройки`):**
  - Configure Graphics Quality (`1–10` or `Auto`), Master Volume, Mouse Sensitivity, and Frame Rate Cap (`15 / 30 / 60 / 120 / 144 / 240 FPS`) via Roblox's native `GlobalBasicSettings_13.xml`.
  - **Lower Background Priority:** optionally sets unfocused Roblox windows to `BelowNormal` CPU priority.
- **⬇ 1-Click GitHub Auto-Updater (`⬇ Обновить`) & Compact Error Logger (`📋 Лог`):**
  - **Auto-Update (`⬇ Обновить`):** checks GitHub Releases for a newer version, downloads the new standalone `.exe`, and restarts the manager seamlessly without closing your running Roblox games.
  - **Compact Diagnostics (`📋 Лог`):** catches global UI, background thread, async task, and first-chance exceptions in a fixed-size 200-entry in-memory ring buffer (zero memory bloat, automatic `.ROBLOSECURITY` scrubbing) that you can export as a compact `.txt` file anytime.

---

## 🔒 Cookie Privacy & Encryption

- Your `.ROBLOSECURITY` cookies are encrypted using **Windows Data Protection API (`DPAPI` with `DataProtectionScope.CurrentUser`)**.
- All configuration and encrypted sessions are stored strictly locally in:
  ```text
  %APPDATA%\RobloxAccountManager\accounts_data.json
  ```
- Cookies are never stored in the application folder or repository, and cannot be decrypted on any other PC or Windows user account.

---

## 📄 License

Licensed under the [Apache License 2.0](LICENSE).
