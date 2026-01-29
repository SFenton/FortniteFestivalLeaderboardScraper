import {parseSongCatalog} from '../epic/contentParsing';

describe('parseSongCatalog', () => {
  test('returns empty on invalid input', () => {
    expect(parseSongCatalog(null)).toEqual([]);
    expect(parseSongCatalog('x')).toEqual([]);
  });

  test('extracts songs with track.su', () => {
    const json = JSON.stringify({
      a: {track: {su: 's1', tt: 'T1', an: 'A1'}},
      b: {track: {su: 's2', tt: 'T2', an: 'A2'}},
      c: {track: {tt: 'bad'}},
    });
    const out = parseSongCatalog(json);
    expect(out.map(s => s.track.su).sort()).toEqual(['s1', 's2']);
  });

  test('tolerates mixed root values and invalid json', () => {
    const json = JSON.stringify({
      n: 123,
      s: 'x',
      o: {foo: 'bar'},
      maybe: {track: {su: ''}},
    });
    expect(parseSongCatalog(json)).toEqual([]);
    expect(parseSongCatalog('{bad')).toEqual([]);
  });
});
