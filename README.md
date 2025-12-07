# ObdInsight

A modern, open-source OBD-II diagnostic application for Android and iOS built with .NET MAUI. 

ObdInsight addresses the poor user experience of existing OBD apps by providing a clean, responsive interface backed by a robust, testable architecture. The pluggable driver system makes it easy to add support for new Bluetooth and WiFi OBD dongles without modifying the core application.

## Why ObdInsight?

- **Better UX**: Designed mobile-first with modern UI patterns
- **Extensible**: Plugin-based driver architecture for easy dongle support
- **Testable**: Drivers can be developed and tested independently of the mobile app
- **Cross-platform**: Single codebase for Android and iOS using .NET MAUI
- **Open**: Community contributions welcome, especially for dongle drivers

## Architecture

ObdInsight separates concerns into testable layers:
- **ObdInsight.Core**: Transport abstractions, driver interfaces, and protocol handling
- **ObdInsight.Drivers**: Built-in support for common dongles (ELM327, etc.)
- **ObdInsight**: The mobile application

Drivers implement a simple interface and can be tested without mobile hardware using mock transports or serial connections.
```

**Topics/tags for GitHub:**
```
obd-ii, elm327, automotive, diagnostics, dotnet-maui, android, ios, bluetooth, vehicle-diagnostics, car-diagnostics