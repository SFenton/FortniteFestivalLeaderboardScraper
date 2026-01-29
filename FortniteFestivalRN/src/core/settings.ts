export type Settings = {
  degreeOfParallelism: number;
  queryLead: boolean;
  queryDrums: boolean;
  queryVocals: boolean;
  queryBass: boolean;
  queryProLead: boolean;
  queryProBass: boolean;
};

export const defaultSettings = (): Settings => ({
  degreeOfParallelism: 16,
  queryLead: true,
  queryDrums: true,
  queryVocals: true,
  queryBass: true,
  queryProLead: true,
  queryProBass: true,
});
