import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / '.tools' / 'fmsave'))
import fmsave

ids = {2002095290, 2000026194, 2000011972, 2002098250, 2002098449, 2002069230}
with fmsave.open(sys.argv[1]) as save:
    print('GAME DATE', save.info.game_date, flush=True)
    players = save.players()
    print('PLAYERS', len(players), flush=True)
    print('2002 UID COUNT', sum(str(p.uid).startswith('2002') for p in players), flush=True)
    print('2002 UNIQUE ID COUNT', sum(str(p.unique_id).startswith('2002') for p in players), flush=True)
    for p in players:
        if p.uid in ids or p.unique_id in ids or (p.last_name in {'Bolton', 'Maieco'} and p.first_name in {'Sam', 'José'}):
            print('FIXTURE', p.uid, p.unique_id, repr(p.first_name), repr(p.last_name), p.age, p.birth_date, repr(p.club_name), p.team_slot, p.nation_id, flush=True)
