load("//rules:cc.star", "cc_library", "cc_binary")

cc_library(name = "base", srcs = ["base.c", "alloc.c"], opt = "O3")
cc_library(name = "net", srcs = ["net.c"], deps = [":base"])
cc_library(name = "ui", srcs = ["ui.c"], deps = [":base"])
cc_binary(name = "server", srcs = ["server.c"], deps = [":net", ":base"])
cc_binary(name = "client", srcs = ["client.c"], deps = [":net", ":ui"], opt = "O0")
