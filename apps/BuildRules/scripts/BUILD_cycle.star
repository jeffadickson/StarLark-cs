load("//rules:cc.star", "cc_library")

cc_library(name = "a", srcs = ["a.c"], deps = ["b"])
cc_library(name = "b", srcs = ["b.c"], deps = ["c"])
cc_library(name = "c", srcs = ["c.c"], deps = ["a"])
