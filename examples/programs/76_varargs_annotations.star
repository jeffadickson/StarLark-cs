# title: *args and **kwargs annotations apply to each element
# dialect: types
# output: 3
# error: does not match the type annotation `int`

def total(*nums: int, **named: int) -> int:
    return sum_all(nums)

def sum_all(nums):
    t = 0
    for n in nums:
        t += n
    return t

print(total(1, 2, a = 3))
total(1, "two")
