"""Windows-only synthetic positive control for the local ETW validation collector."""
import ctypes
import json
import os
import sys

library = ctypes.WinDLL(str(sys.argv[1]))
get_base = library.OrtGetApiBase
get_base.restype = ctypes.POINTER(ctypes.c_void_p)
base = get_base()
get_api = ctypes.WINFUNCTYPE(ctypes.POINTER(ctypes.c_void_p), ctypes.c_uint32)(base[0])
get_version = ctypes.WINFUNCTYPE(ctypes.c_char_p)(base[1])
api = get_api(29)
create_env = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_int, ctypes.c_char_p,
                              ctypes.POINTER(ctypes.c_void_p))(api[3])
environment = ctypes.c_void_p()
assert not create_env(3, b'engram-telemetry-validation-control', ctypes.byref(environment))
print(json.dumps({'pid': os.getpid(), 'version': get_version().decode()}))
