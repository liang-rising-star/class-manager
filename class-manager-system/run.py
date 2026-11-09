import sys
import os

# 支持命令行参数：python run.py --reset
if "--reset" in sys.argv:
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from reset import reset
    reset()
    sys.exit(0)

import uvicorn

if __name__ == "__main__":
    # 解析命令行参数，支持 --port 指定端口
    port = 8000
    if "--port" in sys.argv:
        port_index = sys.argv.index("--port")
        if port_index + 1 < len(sys.argv):
            try:
                port = int(sys.argv[port_index + 1])
            except ValueError:
                pass

    # 仅开发时开启热重载（--dev）；便携/内嵌运行时不要 reload，
    # 否则会多起一个 reloader 子进程并重复初始化数据库与配置。
    reload_enabled = "--dev" in sys.argv
    uvicorn.run("main:app", host="0.0.0.0", port=port, reload=reload_enabled)
