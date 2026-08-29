import { Planned, Screen } from '../components/Screen';

export default function Today() {
  return (
    <Screen title="Today" lede="What needs attention, and what moved since you last looked.">
      <Planned step="built in step 6.3" endpoints={['GET /applications', 'GET /stats/funnel', 'GET /imports']} />
    </Screen>
  );
}
