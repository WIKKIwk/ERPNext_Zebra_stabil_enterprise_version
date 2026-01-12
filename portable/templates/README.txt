ZebraBridge v1 Portable (Linux)
===============================

Quick start (web only):

  ./run.sh

UI: http://127.0.0.1:18000

Terminal TUI (web + TUI):

  ./run.sh --tui

Install as service (auto-start on boot):

  sudo ./install.sh

USB permissions:
- install.sh adds udev rules for /dev/ttyUSB*, /dev/ttyACM*, /dev/usb/lp*

Environment overrides:
- ZEBRA_WEB_HOST=127.0.0.1
- ZEBRA_WEB_PORT=18000
- ZEBRA_ERP_URL, ZEBRA_ERP_AUTH, ZEBRA_ERP_DEVICE
- ZEBRA_API_TOKEN (API auth)

Architecture note:
- This bundle is linux-x64 by default. For arm64, request the arm64 build.
