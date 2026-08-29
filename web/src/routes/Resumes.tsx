import { Planned, Screen } from '../components/Screen';

export default function Resumes() {
  return (
    <Screen title="Résumés" lede="The documents you send, and the skills each one claims.">
      <Planned step="built in step 6.3" endpoints={['GET /resumes', 'GET /resumes/{id}', 'POST /resumes/{id}/skills', 'DELETE /resumes/{id}/skills/{skillName}']} />
    </Screen>
  );
}
