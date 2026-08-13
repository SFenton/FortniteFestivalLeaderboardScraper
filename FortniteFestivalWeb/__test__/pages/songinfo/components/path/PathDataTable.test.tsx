import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import PathDataTable, {
 buildPathRows,
 extractPathInstructions,
 type PathDataResponse,
} from '../../../../../src/pages/songinfo/components/path/PathDataTable';

function pathData(overrides: Partial<PathDataResponse> = {}): PathDataResponse {
  return {
    songName: "What's My Age Again?",
    artist: 'blink-182',
    charter: 'Epic Games',
    difficulty: 'expert',
    totalScore: 100_000,
    pathSummary: [
      'Optimising, please wait...',
      'Path: 2-2-3(+1)-1',
      '2: 1 beats after 14th B (R)',
      '2: 8th G (O)',
      '3(+1): 8th R (B)',
      '1: 4th R (B)',
      'No SP score: 80,000',
      'Total score: 100,000',
    ].join('\n'),
    activations: [],
    notes: [],
    spPhrases: [],
    measures: [],
    bpms: [],
    timeSignatures: [],
    ...overrides,
  };
}

describe('buildPathRows', () => {
  it('keeps legacy mid-sustain activations that have no startNotes', () => {
    const data = pathData({
      activations: [
        {
          startBeat: 98.995,
          endBeat: 114,
          startSeconds: 45.5,
        },
        {
          startBeat: 173.493,
          endBeat: 189,
          startNotes: [{
            beat: 173.5,
            seconds: 80,
            cumulativeScore: 40_100,
            noteValue: 100,
            odPercent: 0.75,
            isSpGranting: false,
          }],
          scoreBeforeActivation: 40_000,
        },
        { startBeat: 278.993, endBeat: 295 },
        { startBeat: 322.993, endBeat: 339 },
      ],
      notes: [
        {
          beat: 98,
          isSpNote: false,
          frets: { red: 2 },
        },
        {
          beat: 173.5,
          isSpNote: false,
          frets: { orange: 0 },
        },
      ],
    });

    const rows = buildPathRows(data);

    expect(rows).toHaveLength(4);
    expect(rows[0]).toMatchObject({
      beat: 98.995,
      seconds: 45.5,
      instruction: '2: 1 beats after 14th B (R)',
      frets: { red: 2 },
    });
    expect(rows[0]?.odPercent).toBeUndefined();
    expect(rows[0]?.cumulativeScore).toBeUndefined();
    expect(rows[1]).toMatchObject({
      beat: 173.5,
      instruction: '2: 8th G (O)',
      odPercent: 75,
      cumulativeScore: 40_000,
      frets: { orange: 0 },
    });
  });

  it('uses schema-v2 activation metadata without synthetic startNotes', () => {
    const rows = buildPathRows(pathData({
      schemaVersion: 2,
      pathSummary: 'Path: 2\n2: 1 beats after NN (R)',
      activations: [{
        instruction: '2: 1 beats after NN (R)',
        startBeat: 20.99,
        endBeat: 36.99,
        activationBeat: 21,
        activationSeconds: 10.5,
        anchorBeat: 20,
        beatsAfterAnchor: 1,
        scoreBeforeActivation: 12_345,
        odAtActivation: 0.5,
      }],
      notes: [{
        beat: 20,
        seconds: 10,
        isSpNote: false,
        frets: { red: 4 },
      }],
    }));

    expect(rows).toEqual([{
      frets: { red: 4 },
      beat: 21,
      seconds: 10.5,
      instruction: '2: 1 beats after NN (R)',
      odPercent: 50,
      cumulativeScore: 12_345,
    }]);
  });

  it('does not guess a legacy fret cue from an unrelated earlier note', () => {
    const rows = buildPathRows(pathData({
      pathSummary: 'Path: 1\n1: 2nd G (B)',
      activations: [{
        startBeat: 80,
        endBeat: 96,
      }],
      notes: [{
        beat: 20,
        isSpNote: false,
        frets: { green: 0 },
      }],
    }));

    expect(rows[0]?.frets).toEqual({});
    expect(rows[0]?.instruction).toBe('1: 2nd G (B)');
  });
});

describe('extractPathInstructions', () => {
  it('removes progress and score metadata while retaining one instruction per activation', () => {
    expect(extractPathInstructions(pathData().pathSummary)).toEqual([
      '2: 1 beats after 14th B (R)',
      '2: 8th G (O)',
      '3(+1): 8th R (B)',
      '1: 4th R (B)',
    ]);
  });
});

describe('PathDataTable', () => {
  it('renders the missing legacy activation instead of dropping the row', () => {
    render(
      <PathDataTable
        data={pathData({
          pathSummary: 'Path: 2\n2: 1 beats after NN (R)',
          activations: [{
            startBeat: 20.99,
            endBeat: 36.99,
            startSeconds: 10.5,
          }],
          notes: [{
            beat: 20,
            isSpNote: false,
            frets: { red: 4 },
          }],
        })}
        isMobile={false}
      />,
    );

    expect(screen.getByText('2: 1 beats after NN (R)')).toBeDefined();
    expect(screen.getByText('20.99')).toBeDefined();
    expect(screen.getAllByText('—')).toHaveLength(2);
  });

  it('renders schema-v2 metrics in the mobile layout', () => {
    render(
      <PathDataTable
        data={pathData({
          schemaVersion: 2,
          pathSummary: 'Path: 2\n2: NN (R)',
          activations: [{
            instruction: '2: NN (R)',
            startBeat: 20.99,
            endBeat: 36.99,
            activationBeat: 21,
            activationSeconds: 10.5,
            anchorBeat: 20,
            scoreBeforeActivation: 12_345,
            odAtActivation: 0.5,
          }],
          notes: [{
            beat: 20,
            isSpNote: false,
            frets: { red: 0 },
          }],
        })}
        isMobile
      />,
    );

    expect(screen.getByText('2: NN (R)')).toBeDefined();
    expect(screen.getByText('00:10:500')).toBeDefined();
    expect(screen.getByText('50%')).toBeDefined();
    expect(screen.getByText('12,345')).toBeDefined();
  });

  it('shows the unavailable state only when there are no activations', () => {
    render(<PathDataTable data={pathData()} isMobile={false} />);

    expect(screen.getByText('Paths not available')).toBeDefined();
  });
});
