import { ScaffolderApiClient } from './client';
import { API_KEY, API_URL } from '../config';

export const apiClient = new ScaffolderApiClient(API_URL, API_KEY);
