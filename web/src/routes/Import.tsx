import { Planned, Screen } from '../components/Screen';

export default function Import() {
  return (
    <Screen title="Import & confirm" lede="Drop in an ad or a CV, check what the parser read, then keep it.">
      <Planned step="built in step 6.3" endpoints={['POST /imports', 'GET /imports', 'GET /imports/{id}', 'PUT /imports/{id}', 'POST /imports/{id}/reparse', 'POST /imports/{id}/confirm', 'DELETE /imports/{id}']} />
    </Screen>
  );
}
