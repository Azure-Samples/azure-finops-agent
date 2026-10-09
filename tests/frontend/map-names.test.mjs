import assert from 'node:assert/strict';
import test from 'node:test';
import { mapFeatureNames, resolveCountryName } from '../../src/Dashboard/frontend/src/mapNames.js';

const feature = (name) => ({ type: 'Feature', properties: { name } });
// The two spellings the app can meet: the ECharts 4.9 map and the world-atlas fallback.
const echarts49 = mapFeatureNames({ features: ['United States', 'Korea', 'Dem. Rep. Korea', 'Czech Rep.', 'Bosnia and Herz.', 'Germany', 'Tanzania'].map(feature) });
const naturalEarth = mapFeatureNames({ features: ['United States of America', 'South Korea', 'North Korea', 'Czechia', 'Bosnia and Herz.', 'Germany', 'Tanzania'].map(feature) });

test('the United States resolves to the spelling the loaded map uses', () => {
  for (const name of ['United States', 'United States of America', 'USA', 'US', 'U.S.A.', 'united states'])
    assert.equal(resolveCountryName(name, echarts49), 'United States', name);
  for (const name of ['United States', 'United States of America', 'USA'])
    assert.equal(resolveCountryName(name, naturalEarth), 'United States of America', name);
});

test('other countries resolve in both map spellings', () => {
  assert.equal(resolveCountryName('South Korea', echarts49), 'Korea');
  assert.equal(resolveCountryName('Korea', naturalEarth), 'South Korea');
  assert.equal(resolveCountryName('DPRK', echarts49), 'Dem. Rep. Korea');
  assert.equal(resolveCountryName('Czech Republic', echarts49), 'Czech Rep.');
  assert.equal(resolveCountryName('Czech Republic', naturalEarth), 'Czechia');
  assert.equal(resolveCountryName('Bosnia', echarts49), 'Bosnia and Herz.');
  assert.equal(resolveCountryName('United Republic of Tanzania', echarts49), 'Tanzania');
});

test('exact, unknown and unusable names are left alone', () => {
  assert.equal(resolveCountryName('Germany', echarts49), 'Germany');
  assert.equal(resolveCountryName('Atlantis', echarts49), 'Atlantis');
  assert.equal(resolveCountryName('United States', new Set()), 'United States');
  assert.equal(resolveCountryName('United States', undefined), 'United States');
  assert.equal(resolveCountryName(undefined, echarts49), undefined);
  assert.deepEqual([...mapFeatureNames(undefined)], []);
});
