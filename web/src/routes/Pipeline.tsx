import { Planned, Screen } from '../components/Screen';

export default function Pipeline() {
  return (
    <Screen title="Pipeline" lede="Drag a card to move it. Some moves are refused, and that is a normal answer.">
      <Planned step="built in step 6.3, and it is the first screen that needs dnd-kit" endpoints={['GET /applications', 'PATCH /applications/{id}']} />
    </Screen>
  );
}
