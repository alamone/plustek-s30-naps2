"""Double-click launcher (runs without a console window via pythonw)."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from scanproc.app import main

main()
