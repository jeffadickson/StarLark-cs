# title: Annotated assignments are checked
# dialect: types
# error: does not match the type annotation `str`

ok: int = 1
fine: list[str] = ["a"]
bad: str = 1
