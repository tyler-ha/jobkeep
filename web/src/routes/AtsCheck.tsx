import { Planned, Screen } from '../components/Screen';

export default function AtsCheck() {
  return (
    <Screen title="ATS check" lede="Hold a résumé against an ad and see exactly what is missing.">
      <Planned step="built in step 6.3, alongside Applications — these two are what the seeded data exercises end to end" endpoints={['GET /resumes', 'PATCH /applications/{id}', 'POST /applications/{id}/ats-check', 'GET /applications/{id}/ats-check', 'POST /resumes/{id}/skills']} />
    </Screen>
  );
}
