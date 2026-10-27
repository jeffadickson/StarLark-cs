load("//rules:cc.star", "cc_binary")

cc_binary(name = "oops", srcs = ["oops.c"], opt = "Ofast")
