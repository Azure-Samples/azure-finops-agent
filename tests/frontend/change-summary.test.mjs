import assert from 'node:assert/strict';
import test from 'node:test';
import { describeChange } from '../../src/Dashboard/frontend/src/changeSummary.js';

test('a PATCH names the changed properties, the resource, its type and group', () => {
  assert.equal(
    describeChange({
      method: 'PATCH',
      target:
        '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-app/providers/Microsoft.Compute/virtualMachines/vm1?api-version=2024-07-01',
      body: '{"tags":{"Owner":"Team"}}',
    }),
    'Update tags on vm1 · Microsoft.Compute/virtualMachines · resource group rg-app',
  );
});

test('nested resource types keep every type segment and properties list their keys', () => {
  assert.equal(
    describeChange({
      method: 'PATCH',
      target:
        '/subscriptions/x/resourceGroups/data/providers/Microsoft.Sql/servers/sql1/databases/db1',
      body: { properties: { autoPauseDelay: 60, minCapacity: 0.5 } },
    }),
    'Update autoPauseDelay and minCapacity on db1 · Microsoft.Sql/servers/databases · resource group data',
  );
});

test('a PUT reads as create or replace and long property lists are shortened', () => {
  assert.equal(
    describeChange({
      method: 'put',
      target: '/subscriptions/x/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/st1',
      body: '{"location":"swedencentral","sku":{"name":"Standard_LRS"},"kind":"StorageV2","tags":{}}',
    }),
    'Create or replace st1 with location, sku, kind and 1 more · Microsoft.Storage/storageAccounts · resource group rg',
  );
});

test('an unparseable body or unknown path still yields a safe summary', () => {
  assert.equal(
    describeChange({ method: 'PATCH', target: '/synthetic/resource/item', body: 'not json' }),
    'Update item',
  );
  assert.equal(describeChange({}), 'Update the resource');
});
