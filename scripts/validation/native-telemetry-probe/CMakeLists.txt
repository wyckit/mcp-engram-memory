cmake_minimum_required(VERSION 3.16)
project(engram_telemetry_probe C)
add_library(onnxruntime SHARED probe.c)
