"""
恢复出厂设置脚本
用法：python reset.py
"""
import os
import json
import secrets

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
DATA_DIR = os.path.join(BASE_DIR, "data")
CONFIG_PATH = os.path.join(DATA_DIR, "config.json")

def get_default_config():
    """返回默认配置，init_required=True"""
    return {
        "system": {
            "name": "班级管理系统",
            "init_required": True,
            "encryption_salt": secrets.token_hex(16)
        },
        "paths": {
            "database": "./data/db/scores.db",
            "output": "./data/output"
        },
        "admin": {
            "username": "",
            "password": ""
        },
        "backup": {
            "enabled": True,
            "time": "02:00",
            "keep": 10
        },
        "desktop": {
            "autostart": False
        },
        "print": {
            "printer": ""
        }
    }

def reset():
    print("=" * 40)
    print("  班级积分系统 - 恢复出厂设置")
    print("=" * 40)
    print()

    # 1. 删除数据库
    if os.path.exists(CONFIG_PATH):
        with open(CONFIG_PATH, "r", encoding="utf-8") as f:
            config = json.load(f)
        db_path = config["paths"]["database"]
        if not os.path.isabs(db_path):
            db_path = os.path.join(BASE_DIR, db_path)
        if os.path.exists(db_path):
            os.remove(db_path)
            print(f"[已删除] 数据库: {db_path}")
        else:
            print(f"[跳过] 数据库不存在: {db_path}")

        # 2. 删除output目录中的文件
        output_dir = config["paths"]["output"]
        if not os.path.isabs(output_dir):
            output_dir = os.path.join(BASE_DIR, output_dir)
        count = 0
        if os.path.isdir(output_dir):
            for f in os.listdir(output_dir):
                fp = os.path.join(output_dir, f)
                if os.path.isfile(fp):
                    try:
                        os.remove(fp)
                        count += 1
                    except PermissionError:
                        print(f"  [跳过] 文件被占用: {f}")
        print(f"[已删除] 导出文件: {count} 个")
    else:
        print("[跳过] 配置文件不存在，无需清理数据库和输出文件")

    # 3. 重置配置
    os.makedirs(DATA_DIR, exist_ok=True)
    with open(CONFIG_PATH, "w", encoding="utf-8") as f:
        json.dump(get_default_config(), f, ensure_ascii=False, indent=2)
    print(f"[已重置] 配置文件: data/config.json")

    print()
    print("恢复出厂设置完成！请重新启动系统。")

if __name__ == "__main__":
    reset()
