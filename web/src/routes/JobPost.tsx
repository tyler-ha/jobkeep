import { Planned, Screen } from '../components/Screen';

export default function JobPost() {
  return (
    <Screen title="Job post" lede="The ad, what it asks for, and what the model made of it.">
      <Planned step="built in step 6.3" endpoints={['GET /applications/{id}', 'POST /applications/{id}/skills', 'POST /applications/{id}/requirements', 'POST /applications/{id}/analyze', 'GET /applications/{id}/analysis']} />
    </Screen>
  );
}
