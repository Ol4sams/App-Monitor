# App Monitor

📡 Утилита для мониторинга и автоматического запуска заданного `.exe` файла.  
💡 Включает обработку диалога безопасности Windows (SmartScreen / Attachment Manager), логирование и определение причин падения.

---

## ⚙️ Возможности

- 🔁 Циклическая проверка процесса по имени и пути
- 🧠 Автоматический запуск, если процесс завершён
- 🪟 Обход диалога SmartScreen (нажатие "Запустить в любом случае")
- ⚠ Определение кода завершения процесса и его расшифровка
- 🕵️ Поиск записей о сбоях в журнале Windows (Event Log → Application Error)

---

## 📝 Конфигурация

Настраивается через `appsettings.json`:

```json
{
  "MonitorSettings": {
    "ExecutableName": "yourapp",
    "ExecutablePath": "C:\\Path\\To\\yourapp.exe",
    "CheckIntervalSeconds": 30
  }
}
