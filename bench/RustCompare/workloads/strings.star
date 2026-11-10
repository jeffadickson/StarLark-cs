def run():
    out = []
    for i in range(6000):
        s = 'item-' + str(i)
        out.append(s.upper() + '%d' % i + '{}'.format(i))
    return len(','.join(out))
r = run()
