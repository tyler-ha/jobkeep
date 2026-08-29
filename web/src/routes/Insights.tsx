import { Planned, Screen } from '../components/Screen';

export default function Insights() {
  return (
    <Screen title="Insights" lede="What the market keeps asking for, and where your applications stall.">
      <Planned step="built in step 6.3" endpoints={['GET /stats/skill-demand', 'GET /stats/funnel', 'GET /stats/companies']} />
    </Screen>
  );
}
